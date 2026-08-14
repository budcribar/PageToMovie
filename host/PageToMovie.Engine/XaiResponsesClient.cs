using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Minimal, standalone client for xAI's Files + Responses API — NOT the same surface as
/// <see cref="PageToMovie.Engine.GrokChatClient"/>, which only talks to the stateless
/// <c>chat/completions</c> endpoint. Product path uses this for Stage‑1 multi-turn
/// so the book is uploaded once (file_id) and follow-ups use previous_response_id.
///
/// Endpoint shapes below were confirmed live against docs.x.ai (not guessed):
///   POST https://api.x.ai/v1/files            (multipart; expires_after must precede file)
///   POST https://api.x.ai/v1/responses         (input_file by file_id on the first turn,
///                                                previous_response_id on follow-ups)
/// </summary>
public sealed class XaiResponsesClient
{
    private const string ApiBase = SupportedModelCatalog.XaiApiBase;
    private const string InputKey = "input";
    private const string ContentKey = "content";

    private readonly HttpClient _http;
    private readonly IUserApiKeyProvider? _keyProvider;

    public XaiResponsesClient(HttpClient http, IUserApiKeyProvider? keyProvider = null)
    {
        _http = http;
        _keyProvider = keyProvider;
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY"));

    public sealed record UploadResult(string FileId, string Filename, long Bytes, long? ExpiresAtUnixSeconds);

    public sealed record SessionTurnResult(
        string ResponseId,
        string OutputText,
        int RequestBytesSent,
        string? UsageJson);

    /// <summary>
    /// Uploads a book once. <paramref name="expiresAfterSeconds"/> defaults to 30 days (the
    /// pipeline doc's recommended retention window) — the xAI-documented range is 3600..2592000.
    /// </summary>
    public async Task<UploadResult> UploadBookAsync(
        string filePath,
        int expiresAfterSeconds = 2592000,
        CancellationToken ct = default)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        return await UploadFileBytesAsync(
            fileBytes, Path.GetFileName(filePath), fallbackFilename: "", expiresAfterSeconds, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Upload in-memory book text (product path; no temp file required).</summary>
    public Task<UploadResult> UploadBookBytesAsync(
        byte[] fileBytes,
        string filename = "book_full.txt",
        int expiresAfterSeconds = 2592000,
        CancellationToken ct = default)
        => UploadFileBytesAsync(
            fileBytes,
            string.IsNullOrWhiteSpace(filename) ? "book_full.txt" : filename,
            fallbackFilename: filename, expiresAfterSeconds, ct);

    /// <summary>
    /// Shared multipart upload to <c>/files</c>. Field order matters: xAI documents that
    /// <c>expires_after</c> must precede <c>file</c> in the multipart body, or the upload is
    /// rejected with a 400. <paramref name="fallbackFilename"/> is used when the response omits a
    /// <c>filename</c> field (each caller preserves its own original fallback).
    /// </summary>
    private async Task<UploadResult> UploadFileBytesAsync(
        byte[] fileBytes,
        string partFileName,
        string fallbackFilename,
        int expiresAfterSeconds,
        CancellationToken ct)
    {
        var key = await RequireApiKeyAsync(ct).ConfigureAwait(false);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(expiresAfterSeconds.ToString()), "expires_after");
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", partFileName);
        form.Add(new StringContent("assistants"), "purpose");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"xAI file upload HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var fileId = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("xAI file upload response had no id.");
        var filename = root.TryGetProperty("filename", out var fn) ? fn.GetString() ?? fallbackFilename : fallbackFilename;
        var bytes = root.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bl) ? bl : fileBytes.LongLength;
        long? expiresAt = root.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.Number
            ? ea.GetInt64()
            : null;
        return new UploadResult(fileId, filename, bytes, expiresAt);
    }

    /// <summary>Upload bytes with no expiry (xAI keeps the file until we delete it).</summary>
    public async Task<UploadResult> UploadPermanentFileAsync(
        byte[] fileBytes,
        string filename,
        string contentType = "video/mp4",
        CancellationToken ct = default)
    {
        if (fileBytes is null || fileBytes.Length == 0)
            throw new ArgumentException("file required", nameof(fileBytes));
        var key = await RequireApiKeyAsync(ct).ConfigureAwait(false);
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        var name = string.IsNullOrWhiteSpace(filename) ? "clip.mp4" : filename;
        form.Add(fileContent, "file", name);
        form.Add(new StringContent("assistants"), "purpose");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"xAI file upload HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var fileId = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("xAI file upload response had no id.");
        return new UploadResult(fileId, name, fileBytes.LongLength, expiresAtUnixSeconds: null);
    }

    /// <summary>First turn with optional system instructions (Responses instructions field).</summary>
    public Task<SessionTurnResult> StartSessionWithSystemAsync(
        string model,
        string fileId,
        string systemPrompt,
        string instructionText,
        CancellationToken ct = default,
        double? temperature = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            [InputKey] = BuildFileInput(instructionText, fileId),
            ["instructions"] = systemPrompt,
        };
        if (temperature is not null) payload["temperature"] = temperature.Value;
        return SendResponsesRequestAsync(payload, ct);
    }

    /// <summary>First turn of a session: attaches the uploaded book by <paramref name="fileId"/>.</summary>
    public Task<SessionTurnResult> StartSessionAsync(
        string model,
        string fileId,
        string instructionText,
        CancellationToken ct = default,
        double? temperature = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            [InputKey] = BuildFileInput(instructionText, fileId),
        };
        if (temperature is not null) payload["temperature"] = temperature.Value;
        return SendResponsesRequestAsync(payload, ct);
    }

    /// <summary>Builds the single-user-turn <c>input</c> array carrying an instruction text plus one
    /// attached file (by id) — the shape shared by the first-turn session starters.</summary>
    private static object[] BuildFileInput(string instructionText, string fileId) => new object[]
    {
        new Dictionary<string, object?>
        {
            ["role"] = "user",
            [ContentKey] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = instructionText },
                new Dictionary<string, object?> { ["type"] = "input_file", ["file_id"] = fileId },
            },
        },
    };

    /// <summary>
    /// A follow-up turn: no book resend, no re-attached file — the provider retains prior context
    /// via <paramref name="previousResponseId"/>. This is the concrete mechanism that proves the
    /// "never send the complete book twice" rule.
    /// </summary>
    public Task<SessionTurnResult> ContinueSessionAsync(
        string model,
        string previousResponseId,
        string instructionText,
        CancellationToken ct = default,
        double? temperature = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["previous_response_id"] = previousResponseId,
            [InputKey] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", [ContentKey] = instructionText },
            },
        };
        if (temperature is not null) payload["temperature"] = temperature.Value;
        return SendResponsesRequestAsync(payload, ct);
    }

    /// <summary>
    /// An independent, non-chained call: attaches one or more already-uploaded files directly by
    /// id, with no <c>previous_response_id</c> at all. Tests whether chaining is actually needed —
    /// referencing a stored file doesn't require conversation continuity, only the upload itself
    /// needs to happen once. Whether the provider discounts repeated file attachments the way
    /// chained context can be cached is exactly the open question this method exists to measure
    /// (see <c>usage.input_tokens_details.cached_tokens</c> on the returned result).
    /// </summary>
    public Task<SessionTurnResult> CompleteWithFilesAsync(
        string model,
        IReadOnlyList<string> fileIds,
        string instructionText,
        CancellationToken ct = default,
        double? temperature = null)
    {
        var content = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = instructionText },
        };
        foreach (var fileId in fileIds)
            content.Add(new Dictionary<string, object?> { ["type"] = "input_file", ["file_id"] = fileId });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            [InputKey] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", [ContentKey] = content },
            },
        };
        if (temperature is not null) payload["temperature"] = temperature.Value;
        return SendResponsesRequestAsync(payload, ct);
    }

    /// <summary>
    /// Same as <see cref="CompleteWithFilesAsync"/> plus a system <c>instructions</c> field
    /// (enrich: book + screenplay attached by file_id, no bodies inlined).
    /// </summary>
    public Task<SessionTurnResult> CompleteWithFilesAndSystemAsync(
        string model,
        IReadOnlyList<string> fileIds,
        string systemPrompt,
        string instructionText,
        CancellationToken ct = default,
        double? temperature = null)
    {
        var content = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = instructionText },
        };
        foreach (var fileId in fileIds)
        {
            if (string.IsNullOrWhiteSpace(fileId)) continue;
            content.Add(new Dictionary<string, object?> { ["type"] = "input_file", ["file_id"] = fileId });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = systemPrompt,
            [InputKey] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", [ContentKey] = content },
            },
        };
        if (temperature is not null) payload["temperature"] = temperature.Value;
        return SendResponsesRequestAsync(payload, ct);
    }

    private async Task<SessionTurnResult> SendResponsesRequestAsync(
        Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        var key = await RequireApiKeyAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/responses")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"xAI responses HTTP {(int)resp.StatusCode}: {Trim(body, 1200)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var responseId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var text = ExtractOutputText(root);
        var usageJson = root.TryGetProperty("usage", out var usage) ? usage.GetRawText() : null;

        return new SessionTurnResult(responseId, text, requestBytes, usageJson);
    }

    /// <summary>
    /// Extracts assistant text from a Responses-API payload: <c>output</c> is an array of items
    /// (e.g. reasoning, message); the message item's <c>content</c> holds <c>output_text</c> parts.
    /// Falls back to a top-level <c>output_text</c> convenience field if the provider includes one.
    /// </summary>
    private static string ExtractOutputText(JsonElement root)
    {
        if (TryReadFlatOutputText(root, out var flat))
            return flat;
        if (TryCollectOutputArrayText(root, out var joined))
            return joined;

        var raw = root.GetRawText();
        return raw.Length <= 2000 ? raw : raw[..2000];
    }

    private static bool TryReadFlatOutputText(JsonElement root, out string text)
    {
        text = "";
        if (!root.TryGetProperty("output_text", out var flat) || flat.ValueKind != JsonValueKind.String)
            return false;
        var s = flat.GetString();
        if (string.IsNullOrEmpty(s))
            return false;
        text = s;
        return true;
    }

    private static bool TryCollectOutputArrayText(JsonElement root, out string text)
    {
        text = "";
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return false;
        var parts = CollectOutputTextParts(output);
        if (parts.Count == 0)
            return false;
        text = string.Join("\n", parts);
        return true;
    }

    private static List<string> CollectOutputTextParts(JsonElement output)
    {
        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
            AppendMessageContentText(parts, item);
        return parts;
    }

    private static void AppendMessageContentText(List<string> parts, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty(ContentKey, out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        foreach (var c in content.EnumerateArray())
            AppendOutputTextPart(parts, c);
    }

    private static void AppendOutputTextPart(List<string> parts, JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object ||
            !c.TryGetProperty("text", out var t) ||
            t.ValueKind != JsonValueKind.String)
            return;
        parts.Add(t.GetString() ?? "");
    }

    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct = default) =>
        ApiKeyScope.Current
        ?? (_keyProvider is not null ? await _keyProvider.GetKeyAsync(null, "grok", ct).ConfigureAwait(false) : null)
        ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private async Task<string> RequireApiKeyAsync(CancellationToken ct = default) =>
        (await ResolveApiKeyAsync(ct).ConfigureAwait(false)) ?? throw new InvalidOperationException(
            "No xAI API key available for Files/Responses (save XAI key in Settings or set XAI_API_KEY).");

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>GET /v1/files/{id}/content — raw bytes. Caller must dispose the stream (closes the HTTP response).</summary>
    public async Task<Stream> OpenFileContentStreamAsync(string fileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw new ArgumentException("file_id required", nameof(fileId));
        var key = await RequireApiKeyAsync(ct).ConfigureAwait(false);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/{Uri.EscapeDataString(fileId.Trim())}/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            throw new InvalidOperationException($"xAI file content HTTP {(int)resp.StatusCode}: {Trim(err, 400)}");
        }
        var inner = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new ResponseOwnedStream(resp, inner);
    }

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly HttpResponseMessage _resp;
        private readonly Stream _inner;
        public ResponseOwnedStream(HttpResponseMessage resp, Stream inner)
        {
            _resp = resp;
            _inner = inner;
        }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _resp.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

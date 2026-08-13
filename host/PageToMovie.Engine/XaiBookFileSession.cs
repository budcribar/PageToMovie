using System.Text;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// xAI Files + Responses session bound to a content-addressed <c>book_id</c>.
/// Persists <c>file_id</c> / <c>last_response_id</c> on <see cref="BookTextRegistryService"/>.
/// </summary>
public sealed class XaiBookFileSession : IBookFileSession
{
    public const string ProviderName = "xai";

    private readonly XaiResponsesClient _client;
    private readonly BookTextRegistryService _registry;
    private readonly string _bookId;
    private readonly string _bookText;
    private readonly Action<string>? _onProgress;

    public XaiBookFileSession(
        XaiResponsesClient client,
        BookTextRegistryService registry,
        string bookId,
        string bookText,
        Action<string>? onProgress = null)
    {
        _client = client;
        _registry = registry;
        _bookId = bookId;
        _bookText = bookText ?? "";
        _onProgress = onProgress;
    }

    public bool IsAvailable => _client.IsConfigured && !string.IsNullOrWhiteSpace(_bookId);
    public string? Provider => ProviderName;
    public string? FileId { get; private set; }
    public string? LastResponseId { get; private set; }

    public async Task EnsureUploadedAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return;

        var existing = await _registry.GetProviderFileAsync(_bookId, ProviderName, ct).ConfigureAwait(false);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.FileId) &&
            (existing.ExpiresAtUnix is null || existing.ExpiresAtUnix > nowUnix + 3600))
        {
            FileId = existing.FileId;
            LastResponseId = existing.LastResponseId;
            _onProgress?.Invoke($"Reusing xAI file_id for {_bookId} (no re-upload).");
            return;
        }

        _onProgress?.Invoke($"Uploading book to xAI Files for {_bookId}…");
        var bytes = Encoding.UTF8.GetBytes(_bookText);
        var upload = await _client.UploadBookBytesAsync(bytes, "book_full.txt", ct: ct).ConfigureAwait(false);
        FileId = upload.FileId;
        LastResponseId = null;
        await _registry.UpsertProviderFileAsync(
            _bookId, ProviderName, upload.FileId, upload.ExpiresAtUnixSeconds, null, ct).ConfigureAwait(false);
        _onProgress?.Invoke($"Saved xAI file_id={upload.FileId} on book_id={_bookId}.");
    }

    public async Task<string> CompletePrimaryAsync(
        string systemPrompt,
        string userInstructionWithoutBookBody,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null)
    {
        await EnsureUploadedAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(FileId))
            throw new InvalidOperationException("xAI file_id missing after upload.");

        _ = mode;
        _ = reasoningEffort;
        var result = await _client.StartSessionWithSystemAsync(
            model, FileId, systemPrompt, userInstructionWithoutBookBody, ct, temperature).ConfigureAwait(false);
        LastResponseId = result.ResponseId;
        await _registry.UpdateLastResponseIdAsync(_bookId, ProviderName, LastResponseId, ct).ConfigureAwait(false);
        _onProgress?.Invoke($"xAI Responses primary response_id={result.ResponseId} (book via file_id).");
        return result.OutputText;
    }

    public async Task<string> CompleteFollowUpAsync(
        string userInstruction,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null)
    {
        _ = mode;
        _ = reasoningEffort;
        if (string.IsNullOrWhiteSpace(LastResponseId))
        {
            // Chain cold: re-attach file
            await EnsureUploadedAsync(ct).ConfigureAwait(false);
            var fileId = FileId;
            if (string.IsNullOrWhiteSpace(fileId))
                throw new InvalidOperationException("xAI file_id missing after upload.");
            var restart = await _client.CompleteWithFilesAsync(
                model, new[] { fileId }, userInstruction, ct, temperature).ConfigureAwait(false);
            LastResponseId = restart.ResponseId;
            await _registry.UpdateLastResponseIdAsync(_bookId, ProviderName, LastResponseId, ct).ConfigureAwait(false);
            _onProgress?.Invoke($"xAI Responses follow-up (re-attach file) response_id={restart.ResponseId}.");
            return restart.OutputText;
        }

        try
        {
            var result = await _client.ContinueSessionAsync(
                model, LastResponseId, userInstruction, ct, temperature).ConfigureAwait(false);
            LastResponseId = result.ResponseId;
            await _registry.UpdateLastResponseIdAsync(_bookId, ProviderName, LastResponseId, ct).ConfigureAwait(false);
            _onProgress?.Invoke($"xAI Responses follow-up response_id={result.ResponseId} (no book resend).");
            return result.OutputText;
        }
        catch (Exception ex)
        {
            _onProgress?.Invoke($"xAI chain failed ({ex.Message}); re-attaching file_id.");
            await EnsureUploadedAsync(ct).ConfigureAwait(false);
            var retryFileId = FileId;
            if (string.IsNullOrWhiteSpace(retryFileId))
                throw new InvalidOperationException("xAI file_id missing after upload.");
            var restart = await _client.CompleteWithFilesAsync(
                model, new[] { retryFileId }, userInstruction, ct, temperature).ConfigureAwait(false);
            LastResponseId = restart.ResponseId;
            await _registry.UpdateLastResponseIdAsync(_bookId, ProviderName, LastResponseId, ct).ConfigureAwait(false);
            return restart.OutputText;
        }
    }
}

public sealed class BookFileSessionFactory : IBookFileSessionFactory
{
    private readonly XaiResponsesClient _xai;
    private readonly BookTextRegistryService _registry;
    private readonly PageToMovieOptions _opts;

    public BookFileSessionFactory(
        XaiResponsesClient xai,
        BookTextRegistryService registry,
        IOptions<PageToMovieOptions> opts)
    {
        _xai = xai;
        _registry = registry;
        _opts = opts.Value;
    }

    public Task<IBookFileSession?> TryCreateAsync(
        string bookId,
        string bookText,
        string modelId,
        CancellationToken ct = default)
    {
        // Fakes mode: never open a real xAI Files + Responses session (that would upload the book
        // to api.x.ai and spend). Return null so Stage 1 falls back to the fake IChatClient path.
        if (_opts.UseFakes)
            return Task.FromResult<IBookFileSession?>(null);
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(bookText))
            return Task.FromResult<IBookFileSession?>(null);
        if (!_xai.IsConfigured)
            return Task.FromResult<IBookFileSession?>(null);

        // Prefer xAI/Grok models for Responses path.
        var entry = SupportedModelCatalog.Find(modelId);
        var id = (modelId ?? "").Trim().ToLowerInvariant();
        var looksXai =
            entry?.Provider == ModelProviderFamily.Xai ||
            id.Contains("grok", StringComparison.Ordinal) ||
            (entry?.ApiBase?.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!looksXai)
            return Task.FromResult<IBookFileSession?>(null);

        IBookFileSession session = new XaiBookFileSession(_xai, _registry, bookId, bookText);
        return Task.FromResult<IBookFileSession?>(session);
    }
}

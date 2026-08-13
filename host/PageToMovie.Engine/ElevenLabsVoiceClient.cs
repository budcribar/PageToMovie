using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// ElevenLabs Instant Voice Clone + TTS.
/// Falls back to a local mock when no API key is configured so studio flows stay demoable.
/// </summary>
public sealed class ElevenLabsVoiceClient : IVoiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ElevenLabsVoiceClient> _log;
    private readonly bool _allowMock;

    public ElevenLabsVoiceClient(
        HttpClient http,
        ILogger<ElevenLabsVoiceClient> log,
        bool allowMockFallback = true)
    {
        _http = http;
        _log = log;
        _allowMock = allowMockFallback;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar);
    }

    public string ProviderId => "elevenlabs";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey()) || _allowMock;

    private static string? ResolveApiKey()
    {
        // Canonical env name is ElevenLabs_API_KEY (catalog + user docs).
        // Also accept all-caps ELEVENLABS_API_KEY for older shells/deploy scripts.
        var key = ApiKeyScope.CurrentElevenLabs
                  ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.ElevenLabsApiKeyEnv)
                  ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        // Strip a single pair of surrounding double-quotes if the user pasted one.
        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
            key = key[1..^1].Trim();
        return key;
    }

    public async Task<VoiceCloneResult> CreateCloneAsync(
        string displayName,
        byte[] sampleAudio,
        string sampleFileName,
        CancellationToken ct = default)
    {
        if (sampleAudio is null || sampleAudio.Length < 64)
            return new VoiceCloneResult { Ok = false, Error = "Sample audio is empty or too short." };

        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!_allowMock)
                return new VoiceCloneResult { Ok = false, Error = "ElevenLabs_API_KEY not configured." };
            var mockId = "mock_clone_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sampleAudio))[..12].ToLowerInvariant();
            _log.LogInformation("ElevenLabs key missing — mock clone {VoiceId} for {Name}", mockId, displayName);
            return new VoiceCloneResult
            {
                Ok = true,
                ProviderVoiceId = mockId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Mock clone" : displayName.Trim(),
                UsedMock = true,
            };
        }

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(string.IsNullOrWhiteSpace(displayName) ? "PageToMovie narrator" : displayName.Trim()), "name");
            form.Add(new StringContent("Cloned for PageToMovie character dialogue"), "description");
            var fileName = string.IsNullOrWhiteSpace(sampleFileName) ? "sample.wav" : Path.GetFileName(sampleFileName);
            var streamContent = new ByteArrayContent(sampleAudio);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(GuessAudioMime(fileName));
            form.Add(streamContent, "files", fileName);

            using var req = new HttpRequestMessage(HttpMethod.Post, "voices/add");
            req.Headers.TryAddWithoutValidation("xi-api-key", key);
            req.Content = form;

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ElevenLabs clone failed {Status}: {Body}", (int)resp.StatusCode, Trunc(body));
                return new VoiceCloneResult { Ok = false, Error = FormatCloneError((int)resp.StatusCode, body) };
            }

            using var doc = JsonDocument.Parse(body);
            var voiceId = doc.RootElement.TryGetProperty("voice_id", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(voiceId))
                return new VoiceCloneResult { Ok = false, Error = "ElevenLabs response missing voice_id." };

            return new VoiceCloneResult
            {
                Ok = true,
                ProviderVoiceId = voiceId,
                DisplayName = displayName,
                UsedMock = false,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ElevenLabs clone exception");
            return new VoiceCloneResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<VoiceTtsResult> TextToSpeechAsync(
        string providerVoiceId,
        string text,
        string? modelId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerVoiceId))
            return new VoiceTtsResult { Ok = false, Error = "providerVoiceId required" };
        if (string.IsNullOrWhiteSpace(text))
            return new VoiceTtsResult { Ok = false, Error = "text required" };

        var key = ResolveApiKey();
        var isMockVoice = providerVoiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(key) || isMockVoice)
        {
            if (!_allowMock && string.IsNullOrWhiteSpace(key))
                return new VoiceTtsResult { Ok = false, Error = "ElevenLabs_API_KEY not configured." };
            var wav = MockToneWav.FromText(text);
            return new VoiceTtsResult
            {
                Ok = true,
                AudioBytes = wav,
                ContentType = "audio/wav",
                FileExtension = ".wav",
                UsedMock = true,
            };
        }

        try
        {
            var model = ProjectModelSelection.RequireExplicit(modelId, ModelCapability.Voice, "ElevenLabs TTS");
            var payload = JsonSerializer.Serialize(new
            {
                text = text.Trim(),
                model_id = model,
                voice_settings = new { stability = 0.45, similarity_boost = 0.75 },
            });

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"text-to-speech/{Uri.EscapeDataString(providerVoiceId)}");
            req.Headers.TryAddWithoutValidation("xi-api-key", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("ElevenLabs TTS failed {Status}: {Body}", (int)resp.StatusCode, Trunc(err));
                return new VoiceTtsResult { Ok = false, Error = $"ElevenLabs TTS failed ({(int)resp.StatusCode}): {Trunc(err)}" };
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return new VoiceTtsResult
            {
                Ok = true,
                AudioBytes = bytes,
                ContentType = "audio/mpeg",
                FileExtension = ".mp3",
                UsedMock = false,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ElevenLabs TTS exception");
            return new VoiceTtsResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<IReadOnlyList<VoiceCatalogEntry>> ListVoicesAsync(CancellationToken ct = default)
    {
        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new[]
            {
                new VoiceCatalogEntry
                {
                    ProviderVoiceId = "mock_premade_adam",
                    Name = "Mock Adam (demo)",
                    Category = "premade",
                    IsCloned = false,
                },
                new VoiceCatalogEntry
                {
                    ProviderVoiceId = "mock_premade_rachel",
                    Name = "Mock Rachel (demo)",
                    Category = "premade",
                    IsCloned = false,
                },
            };
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "voices");
            req.Headers.TryAddWithoutValidation("xi-api-key", key);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ElevenLabs list voices failed {Status}: {Body}", (int)resp.StatusCode, Trunc(body));
                return Array.Empty<VoiceCatalogEntry>();
            }

            using var doc = JsonDocument.Parse(body);
            var list = new List<VoiceCatalogEntry>();
            if (doc.RootElement.TryGetProperty("voices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var id = v.TryGetProperty("voice_id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var name = v.TryGetProperty("name", out var nEl) ? nEl.GetString() : id;
                    var cat = v.TryGetProperty("category", out var cEl) ? cEl.GetString() : null;
                    string? preview = null;
                    if (v.TryGetProperty("preview_url", out var pEl))
                        preview = pEl.GetString();
                    list.Add(new VoiceCatalogEntry
                    {
                        ProviderVoiceId = id,
                        Name = name ?? id,
                        Category = cat,
                        PreviewUrl = preview,
                        IsCloned = string.Equals(cat, "cloned", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ElevenLabs list voices exception");
            return Array.Empty<VoiceCatalogEntry>();
        }
    }


    /// <summary>Map provider JSON into a short, operator-facing message (no raw payload dump).</summary>
    public static string FormatCloneError(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var detail))
            {
                var statusCode = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("status", out var st)
                    ? st.GetString()
                    : null;
                var msg = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
                var code = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var c)
                    ? c.GetString()
                    : null;

                if (string.Equals(statusCode, "missing_permissions", StringComparison.OrdinalIgnoreCase)
                    || (msg?.Contains("create_instant_voice_clone", StringComparison.OrdinalIgnoreCase) ?? false)
                    || (msg?.Contains("instant_voice_clone", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    return "This ElevenLabs key cannot create Instant Voice Clones "
                           + "(missing Instant Voice Cloning permission). "
                           + "Fix the key/plan in ElevenLabs, or in Settings choose another voice model yourself. "
                           + "Your recording is saved — we do not switch providers automatically.";
                }
                if (status == 401 || string.Equals(statusCode, "authentication_error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return "ElevenLabs rejected this API key (unauthorized). "
                           + "Check the key in Settings, or pick a different voice model yourself.";
                }
                if (!string.IsNullOrWhiteSpace(msg))
                    return "ElevenLabs: " + msg.Trim();
            }
        }
        catch
        {
            // fall through
        }
        return $"ElevenLabs clone failed ({status}). Check the key and Instant Voice Cloning permission in Settings.";
    }

    private static string GuessAudioMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            _ => "application/octet-stream",
        };

    private static string Trunc(string? s, int max = 240) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Tiny PCM WAV generator for mock TTS / sample seed audio.</summary>
public static class MockToneWav
{
    public static byte[] FromText(string text)
    {
        // Map text length to duration (0.6s–4s) and pitch so samples differ.
        var hash = text.GetHashCode();
        var secs = Math.Clamp(0.6 + (Math.Abs(hash) % 35) / 10.0, 0.6, 4.0);
        var freq = 180 + (Math.Abs(hash / 7) % 220);
        return Sine(secs, freq);
    }

    public static byte[] Sine(double seconds, double frequencyHz, int sampleRate = 22050)
    {
        var n = Math.Max(1, (int)(seconds * sampleRate));
        var data = new byte[44 + n * 2];
        // RIFF header
        WriteAscii(data, 0, "RIFF");
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), 36 + n * 2);
        WriteAscii(data, 8, "WAVE");
        WriteAscii(data, 12, "fmt ");
        BitConverter.TryWriteBytes(data.AsSpan(16, 4), 16); // PCM chunk size
        BitConverter.TryWriteBytes(data.AsSpan(20, 2), (short)1); // PCM
        BitConverter.TryWriteBytes(data.AsSpan(22, 2), (short)1); // mono
        BitConverter.TryWriteBytes(data.AsSpan(24, 4), sampleRate);
        BitConverter.TryWriteBytes(data.AsSpan(28, 4), sampleRate * 2);
        BitConverter.TryWriteBytes(data.AsSpan(32, 2), (short)2); // block align
        BitConverter.TryWriteBytes(data.AsSpan(34, 2), (short)16); // bits
        WriteAscii(data, 36, "data");
        BitConverter.TryWriteBytes(data.AsSpan(40, 4), n * 2);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)sampleRate;
            // Soft envelope so it doesn't click
            var env = Math.Min(1.0, Math.Min(t * 8, (seconds - t) * 8));
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * 0.35 * env * short.MaxValue);
            BitConverter.TryWriteBytes(data.AsSpan(44 + i * 2, 2), sample);
        }
        return data;
    }

    private static void WriteAscii(byte[] buf, int offset, string s)
    {
        for (var i = 0; i < s.Length; i++)
            buf[offset + i] = (byte)s[i];
    }
}

using System.Text.Json;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Shared chat-client HTTP finish/telemetry and the Grok-routed vision stubs used by
/// Anthropic/Gemini. Provider request shapes and 400 self-heal stay in each client.
/// </summary>
internal static class ChatClientHelpers
{
    public const int TelemetryErrorTrim = 800;
    public const int ResponsePreviewMax = 2000;

    public static Task<string> TranscribePageNotSupported(string provider) =>
        throw new NotSupportedException(
            $"Book-page transcription is not implemented for {provider} yet — route this call to Grok.");

    public static Task<CharacterPageClassification> ClassifyCharactersNotSupported(string provider) =>
        throw new NotSupportedException(
            $"Character-page classification is not implemented for {provider} yet — route this call to Grok.");

    /// <summary>
    /// Logs HTTP failure telemetry + throws, or parses assistant text and logs success telemetry.
    /// <paramref name="rec"/> should already have Kind, Mode, Endpoint, Model, DurationMs,
    /// SystemPrompt, UserPrompt, PromptChars, and Attempt. This method sets HttpStatus,
    /// Error/Ok, ResponsePreview, and ResponseChars from the response.
    /// Callers that need a different model id on HTTP failure (normalized vs requested)
    /// set <see cref="ApiCallTelemetry.Model"/> on the record they pass.
    /// </summary>
    public static async Task<string> FinishChatResponseAsync(
        ProjectTelemetryService telemetry,
        HttpResponseMessage resp,
        string body,
        ApiCallTelemetry rec,
        Func<JsonElement, string> extractText,
        string httpErrorPrefix,
        CancellationToken ct)
    {
        rec.HttpStatus = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode)
        {
            rec.Error = ProviderHttpHelpers.Trim(body, TelemetryErrorTrim);
            rec.Ok = false;
            await telemetry.LogApiCallAsync(rec, ct).ConfigureAwait(false);
            throw ChatHttpStatusException.FromResponse(resp,
                $"{httpErrorPrefix} HTTP {(int)resp.StatusCode}: {ProviderHttpHelpers.Trim(body, TelemetryErrorTrim)}");
        }

        using var doc = JsonDocument.Parse(body);
        var text = extractText(doc.RootElement);
        rec.ResponsePreview = ProviderHttpHelpers.Trim(text, ResponsePreviewMax);
        rec.ResponseChars = text.Length;
        rec.Ok = true;
        await telemetry.LogApiCallAsync(rec, ct).ConfigureAwait(false);
        return text;
    }

    public static Task LogChatExceptionAsync(
        ProjectTelemetryService telemetry,
        Exception ex,
        ApiCallTelemetry rec,
        CancellationToken ct)
    {
        rec.Error = ex.Message;
        rec.Ok = false;
        return telemetry.LogApiCallAsync(rec, ct);
    }

    /// <summary>Filename list logged on clip auto-review vision calls (Anthropic/Gemini).</summary>
    public static string ImageNamesForLog(IReadOnlyList<string> imagePaths) =>
        string.Join(", ", imagePaths.Select(Path.GetFileName));
}

/// <summary>
/// Book-page OCR / cast classification stay on <see cref="GrokVisionClient"/>.
/// Anthropic and Gemini implement <see cref="IVisionClient"/> by inheriting this
/// so the stub methods are not cloned across clients.
/// </summary>
public abstract class ChatProviderWithoutBookVision : IVisionClient
{
    protected abstract string UnsupportedVisionProvider { get; }

    public abstract bool IsConfigured { get; }

    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "", CancellationToken ct = default) =>
        ChatClientHelpers.TranscribePageNotSupported(UnsupportedVisionProvider);

    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "", CancellationToken ct = default) =>
        ChatClientHelpers.ClassifyCharactersNotSupported(UnsupportedVisionProvider);

    public abstract Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        double temperature = 0.0,
        CancellationToken ct = default);
}

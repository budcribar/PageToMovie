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
    /// <paramref name="errorModel"/> is the model id recorded on HTTP failure (Gemini logs the
    /// normalized id there and the caller-supplied id on success); null uses <paramref name="model"/>.
    /// </summary>
    public static async Task<string> FinishChatResponseAsync(
        ProjectTelemetryService telemetry,
        HttpResponseMessage resp,
        string body,
        string kind,
        string? mode,
        string endpoint,
        string model,
        string? errorModel,
        long durationMs,
        string? systemPrompt,
        string? userPrompt,
        int promptChars,
        int attempt,
        Func<JsonElement, string> extractText,
        string httpErrorPrefix,
        CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            await telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = kind,
                Mode = mode,
                Endpoint = endpoint,
                Model = errorModel ?? model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = durationMs,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                PromptChars = promptChars,
                Attempt = attempt,
                Error = ProviderHttpHelpers.Trim(body, TelemetryErrorTrim),
                Ok = false,
            }, ct).ConfigureAwait(false);
            throw ChatHttpStatusException.FromResponse(resp,
                $"{httpErrorPrefix} HTTP {(int)resp.StatusCode}: {ProviderHttpHelpers.Trim(body, TelemetryErrorTrim)}");
        }

        using var doc = JsonDocument.Parse(body);
        var text = extractText(doc.RootElement);
        await telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = kind,
            Mode = mode,
            Endpoint = endpoint,
            Model = model,
            HttpStatus = (int)resp.StatusCode,
            DurationMs = durationMs,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            PromptChars = promptChars,
            Attempt = attempt,
            ResponsePreview = ProviderHttpHelpers.Trim(text, ResponsePreviewMax),
            ResponseChars = text.Length,
            Ok = true,
        }, ct).ConfigureAwait(false);
        return text;
    }

    public static Task LogChatExceptionAsync(
        ProjectTelemetryService telemetry,
        Exception ex,
        string kind,
        string? mode,
        string endpoint,
        string model,
        long durationMs,
        string? systemPrompt,
        string? userPrompt,
        int? attempt,
        CancellationToken ct) =>
        telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = kind,
            Mode = mode,
            Endpoint = endpoint,
            Model = model,
            DurationMs = durationMs,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Attempt = attempt,
            Error = ex.Message,
            Ok = false,
        }, ct);

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
        CancellationToken ct = default);
}

using System.Text.Json;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Shared chat-client HTTP finish/telemetry and the Grok-routed vision stubs used by
/// Anthropic/Gemini. Provider request shapes and 400 self-heal stay in each client.
/// </summary>
internal sealed record ChatCallContext(
    ProjectTelemetryService Telemetry,
    string Kind,
    string? Mode,
    string Endpoint,
    string Model,
    string? SystemPrompt,
    string? UserPrompt);

internal sealed record ChatHttpFinish(
    HttpResponseMessage Response,
    string Body,
    long DurationMs,
    int Attempt,
    int PromptChars,
    Func<JsonElement, string> ExtractText,
    string HttpErrorPrefix,
    CancellationToken Ct);

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
        ChatCallContext ctx,
        ChatHttpFinish http,
        string? errorModel = null)
    {
        if (!http.Response.IsSuccessStatusCode)
        {
            await ctx.Telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = ctx.Kind,
                Mode = ctx.Mode,
                Endpoint = ctx.Endpoint,
                Model = errorModel ?? ctx.Model,
                HttpStatus = (int)http.Response.StatusCode,
                DurationMs = http.DurationMs,
                SystemPrompt = ctx.SystemPrompt,
                UserPrompt = ctx.UserPrompt,
                PromptChars = http.PromptChars,
                Attempt = http.Attempt,
                Error = ProviderHttpHelpers.Trim(http.Body, TelemetryErrorTrim),
                Ok = false,
            }, http.Ct).ConfigureAwait(false);
            throw ChatHttpStatusException.FromResponse(http.Response,
                $"{http.HttpErrorPrefix} HTTP {(int)http.Response.StatusCode}: {ProviderHttpHelpers.Trim(http.Body, TelemetryErrorTrim)}");
        }

        using var doc = JsonDocument.Parse(http.Body);
        var text = http.ExtractText(doc.RootElement);
        await ctx.Telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = ctx.Kind,
            Mode = ctx.Mode,
            Endpoint = ctx.Endpoint,
            Model = ctx.Model,
            HttpStatus = (int)http.Response.StatusCode,
            DurationMs = http.DurationMs,
            SystemPrompt = ctx.SystemPrompt,
            UserPrompt = ctx.UserPrompt,
            PromptChars = http.PromptChars,
            Attempt = http.Attempt,
            ResponsePreview = ProviderHttpHelpers.Trim(text, ResponsePreviewMax),
            ResponseChars = text.Length,
            Ok = true,
        }, http.Ct).ConfigureAwait(false);
        return text;
    }

    public static Task LogChatExceptionAsync(
        ChatCallContext ctx,
        Exception ex,
        long durationMs,
        CancellationToken ct,
        int? attempt = null) =>
        ctx.Telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = ctx.Kind,
            Mode = ctx.Mode,
            Endpoint = ctx.Endpoint,
            Model = ctx.Model,
            DurationMs = durationMs,
            SystemPrompt = ctx.SystemPrompt,
            UserPrompt = ctx.UserPrompt,
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

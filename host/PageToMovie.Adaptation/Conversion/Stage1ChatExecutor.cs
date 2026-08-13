using PageToMovie.Core.Abstractions;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Ambient book file session for Stage‑1 (set by <c>ConvertWithMetadataAsync</c>).
/// Avoids threading optional session through every private helper.
/// </summary>
internal static class Stage1BookSessionScope
{
    private static readonly AsyncLocal<IBookFileSession?> Holder = new();
    public static IBookFileSession? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}

/// <summary>
/// Ambient Fountain file session for Stage‑1 merge / repairs (set by <c>ConvertWithMetadataAsync</c>).
/// </summary>
internal static class Stage1FountainSessionScope
{
    private static readonly AsyncLocal<IFountainFileSession?> Holder = new();
    public static IFountainFileSession? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}

/// <summary>
/// Stage‑1 chat loop: primary → optional validation correction → deterministic fallback.
/// Mirrors Engine Stage1FountainLifecycle behavior without ModelExecution dependencies.
/// When an <see cref="IBookFileSession"/> is available, the book rides on file_id /
/// previous_response_id so follow-ups (coverage, correction, merge, repair) do not re-bill
/// full book tokens.
/// </summary>
internal static class Stage1ChatExecutor
{
    internal sealed record Request(
        string SystemPrompt,
        string UserPrompt,
        string Model,
        double Temperature,
        string Mode,
        string PromptVersion,
        string CorrectionInstruction,
        string? ReasoningEffort = null,
        string? DeterministicFallback = null,
        string OperationName = "stage1_book_to_fountain");

    internal sealed record Response(string FountainPackage);

    internal sealed record Result(
        Response? Value,
        Stage1ResultSource Source,
        bool Success);

    internal static async Task<Result> ExecuteAsync(
        IChatClient chat,
        Request request,
        Func<string, IReadOnlyList<Stage1ValidationIssue>> validate,
        CancellationToken ct,
        IBookFileSession? bookSession = null,
        IFountainFileSession? fountainSession = null)
    {
        fountainSession ??= Stage1FountainSessionScope.Current;
        bookSession ??= Stage1BookSessionScope.Current;

        // Fountain file (merge/repairs) wins over book chain — do not paste 60k words
        // into a book follow-up.
        string? primaryRaw;
        if (fountainSession is { IsAvailable: true })
        {
            primaryRaw = await fountainSession.CompleteAsync(
                request.SystemPrompt, request.UserPrompt, request.Model,
                request.Temperature, ct).ConfigureAwait(false);
        }
        else if (bookSession is { IsAvailable: true })
        {
            // Later Stage‑1 ops (chunk 2+, merge, repair) reuse previous_response_id.
            if (!string.IsNullOrWhiteSpace(bookSession.LastResponseId))
            {
                primaryRaw = await bookSession.CompleteFollowUpAsync(
                    request.UserPrompt, request.Model, request.Temperature, ct,
                    request.Mode, request.ReasoningEffort).ConfigureAwait(false);
            }
            else
            {
                primaryRaw = await bookSession.CompletePrimaryAsync(
                    request.SystemPrompt, request.UserPrompt, request.Model,
                    request.Temperature, ct, request.Mode, request.ReasoningEffort).ConfigureAwait(false);
            }
        }
        else
        {
            primaryRaw = await CompleteWithTransientRetryAsync(
                chat, request.SystemPrompt, request.UserPrompt, request.Model,
                request.Temperature, request.Mode, request.ReasoningEffort, ct).ConfigureAwait(false);
        }

        var primaryCleaned = BookToFountainConverter.StripBookPageTags(
            BookToFountainConverter.StripFences(primaryRaw ?? ""));
        var primaryIssues = validate(primaryCleaned)
            .Where(i => !string.IsNullOrWhiteSpace(i.Code))
            .ToList();

        if (!string.IsNullOrWhiteSpace(primaryCleaned) && primaryIssues.Count == 0)
        {
            return new Result(new Response(primaryCleaned), Stage1ResultSource.PrimaryResponse, Success: true);
        }

        // One corrective attempt
        var correctionUser = request.UserPrompt + $"""


            CORRECTIVE ATTEMPT ({request.PromptVersion})
            {request.CorrectionInstruction}
            Validation findings:
            {string.Join("\n", primaryIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"))}
            Return the complete corrected Fountain package only. Do not return a patch list.
            Previous response:
            --- BEGIN PREVIOUS RESPONSE ---
            {primaryRaw}
            --- END PREVIOUS RESPONSE ---
            """;

        var corrTemp = Math.Min(request.Temperature, 0.15);
        string? correctiveRaw = null;
        try
        {
            if (fountainSession is { IsAvailable: true })
            {
                var findings = string.Join("\n", primaryIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"));
                var shortCorrection =
                    $"CORRECTIVE ATTEMPT ({request.PromptVersion})\n" +
                    $"{request.CorrectionInstruction}\n" +
                    $"Validation findings:\n{findings}\n" +
                    "The Fountain is still attached. Return the complete corrected Fountain only.";
                correctiveRaw = await fountainSession.CompleteAsync(
                    request.SystemPrompt, shortCorrection, request.Model, corrTemp, ct)
                    .ConfigureAwait(false);
            }
            else if (bookSession is { IsAvailable: true })
            {
                // Follow-up: only correction instructions — no book body re-send.
                var findings = string.Join("\n", primaryIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"));
                var shortCorrection =
                    $"CORRECTIVE ATTEMPT ({request.PromptVersion})\n" +
                    $"{request.CorrectionInstruction}\n" +
                    $"Validation findings:\n{findings}\n" +
                    "Return the complete corrected Fountain package only. Do not return a patch list.";
                correctiveRaw = await bookSession.CompleteFollowUpAsync(
                    shortCorrection, request.Model, corrTemp, ct,
                    request.Mode + "_correction", request.ReasoningEffort).ConfigureAwait(false);
            }
            else
            {
                correctiveRaw = await CompleteWithTransientRetryAsync(
                    chat, request.SystemPrompt, correctionUser, request.Model,
                    corrTemp, request.Mode + "_correction", request.ReasoningEffort, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            /* fall through to deterministic */
        }

        if (correctiveRaw is not null)
        {
            var corrCleaned = BookToFountainConverter.StripBookPageTags(
                BookToFountainConverter.StripFences(correctiveRaw));
            var corrIssues = validate(corrCleaned);
            if (!string.IsNullOrWhiteSpace(corrCleaned) && corrIssues.Count == 0)
            {
                return new Result(new Response(corrCleaned), Stage1ResultSource.CorrectiveResponse, Success: true);
            }
        }

        if (request.DeterministicFallback is not null)
        {
            return new Result(
                new Response(request.DeterministicFallback),
                Stage1ResultSource.DeterministicFallback,
                Success: true);
        }

        return new Result(null, Stage1ResultSource.Failed, Success: false);
    }

    private static async Task<string> CompleteWithTransientRetryAsync(
        IChatClient chat,
        string system,
        string user,
        string model,
        double temperature,
        string mode,
        string? reasoningEffort,
        CancellationToken ct,
        int maxAttempts = 2)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await chat.CompleteAsync(
                    system, user, model, temperature, ct,
                    mode: mode, reasoningEffort: reasoningEffort).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                last = ex;
                await Task.Delay(200 * attempt * attempt, ct).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("Stage1 chat failed.");
    }

    private static bool IsTransient(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("429", StringComparison.Ordinal) ||
            msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("503", StringComparison.Ordinal) ||
            msg.Contains("502", StringComparison.Ordinal))
            return true;
        return ex is HttpRequestException or TaskCanceledException or TimeoutException;
    }
}

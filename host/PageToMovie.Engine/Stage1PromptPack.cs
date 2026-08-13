namespace PageToMovie.Engine;

/// <summary>
/// Loads the book → Fountain prompt pack (<c>prompts/book_to_fountain.txt</c>).
/// Fountain is the operator screenplay; there is no book→JSON scene-bible prompt.
/// </summary>
public static class Stage1PromptPack
{
    /// <summary>Primary prompt for book → Fountain.</summary>
    public const string BookToFountainRelativePath = "prompts/book_to_fountain.txt";

    /// <summary>
    /// System prompt for book → Fountain. Loads <c>prompts/book_to_fountain.txt</c>
    /// with <c>{{TOTAL_RUNTIME_MINUTES}}</c> substituted.
    /// </summary>
    public static async Task<string> LoadBookToFountainSystemPromptAsync(
        string workspaceRoot,
        int totalRuntimeMinutes,
        string? fallbackBody = null,
        CancellationToken ct = default)
    {
        totalRuntimeMinutes = Math.Clamp(totalRuntimeMinutes, 3, 180);

        string body;
        try
        {
            body = await PromptFiles.ReadAsync(BookToFountainRelativePath, workspaceRoot, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (fallbackBody is string fb && !string.IsNullOrWhiteSpace(fb))
        {
            body = fb;
        }

        return body.Replace("{{TOTAL_RUNTIME_MINUTES}}", totalRuntimeMinutes.ToString());
    }
}

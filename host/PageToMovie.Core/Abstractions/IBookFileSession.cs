namespace PageToMovie.Core.Abstractions;

/// <summary>
/// Provider-side book handle for Stage‑1 multi-turn: upload once (<c>file_id</c>),
/// then follow-ups via <c>previous_response_id</c> so the full book is not re-billed.
/// Implemented by Engine (xAI Files + Responses); optional for Adaptation.
/// </summary>
public interface IBookFileSession
{
    /// <summary>True when provider supports file attach + response chaining for this model.</summary>
    bool IsAvailable { get; }

    string? Provider { get; }
    string? FileId { get; }
    string? LastResponseId { get; }

    /// <summary>
    /// Ensure the book text is uploaded (or a valid cached file_id is reused). Call before primary.
    /// </summary>
    Task EnsureUploadedAsync(CancellationToken ct = default);

    /// <summary>
    /// First turn: system + instruction; book attached as <c>input_file</c> (not inlined).
    /// </summary>
    Task<string> CompletePrimaryAsync(
        string systemPrompt,
        string userInstructionWithoutBookBody,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null);

    /// <summary>
    /// Follow-up turn (correction / coverage / merge / repair): no book resend.
    /// Requires a prior <see cref="CompletePrimaryAsync"/> (or persisted last_response_id).
    /// Falls back to primary with re-attach if chain is dead.
    /// </summary>
    Task<string> CompleteFollowUpAsync(
        string userInstruction,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null);

    /// <summary>
    /// Instruction with the book file plus extra artifacts (index, stitch) attached.
    /// Does not use previous_response_id — each call is a fresh turn.
    /// </summary>
    Task<string> CompleteWithFilesAsync(
        string systemPrompt,
        string userInstruction,
        IReadOnlyList<string> extraFileIds,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default);
}

/// <summary>Factory used by Engine orchestration to open a session for a registered book.</summary>
public interface IBookFileSessionFactory
{
    /// <summary>
    /// Create a session when the planning model is xAI/Grok Responses-capable; otherwise null
    /// (caller falls back to plain <see cref="IChatClient"/> with inlined book text).
    /// </summary>
    Task<IBookFileSession?> TryCreateAsync(
        string bookId,
        string bookText,
        string modelId,
        CancellationToken ct = default);
}

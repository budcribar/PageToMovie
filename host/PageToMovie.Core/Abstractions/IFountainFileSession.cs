namespace PageToMovie.Core.Abstractions;

/// <summary>
/// Provider-side handle for a generated Fountain draft: upload once (<c>file_id</c>),
/// then merge / name / location / narration repairs attach the file instead of pasting
/// the full screenplay into chat.
/// </summary>
public interface IFountainFileSession
{
    bool IsAvailable { get; }
    string? FileId { get; }

    /// <summary>Upload or reuse by content SHA. Call before <see cref="CompleteAsync"/>.</summary>
    Task EnsureUploadedAsync(string fountainText, CancellationToken ct = default);

    /// <summary>
    /// Instruction + attached Fountain file (no body inlined).
    /// Re-attaches the current <see cref="FileId"/> each call.
    /// </summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        string instructionWithoutFountainBody,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default);
}

/// <summary>Engine factory — bound to a project dir so SHA reuse can persist.</summary>
public interface IFountainFileSessionFactory
{
    IFountainFileSession? TryCreate(string projectDir, string modelId);
}

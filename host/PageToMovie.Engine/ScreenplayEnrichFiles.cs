using System.Text;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Attach the book and/or screenplay via xAI Files (<c>file_id</c>) instead of pasting bodies
/// into chat/completions. Reuses stored handles when the content SHA still matches.
/// </summary>
public static class ScreenplayEnrichFiles
{
    public const string EnrichInstruction =
        "Two files are attached:\n" +
        "1) book_full.txt — original book. Use it only to ground visual / spatial / atmospheric detail. Do not add plot.\n" +
        "2) screenplay.max.fountain — the Fountain draft to enrich.\n" +
        "Return the complete enriched Fountain screenplay. Dialogue, cues, and scene headings stay the same.";

    public const string ReskinInstruction =
        "The attached file is screenplay.max.fountain — the Fountain draft to re-skin.\n" +
        "Rewrite only the descriptive layer for the target visual medium in the system prompt.\n" +
        "Return the complete Fountain. Dialogue, cues, and scene headings stay the same.";

    public static string TrimInstruction(int targetMinutes, int naturalMinutes) =>
        "The attached file is screenplay.max.fountain — the full-length Fountain draft to trim.\n" +
        $"Trim toward ~{Math.Max(1, targetMinutes)} min (natural length ~{Math.Max(1, naturalMinutes)} min).\n" +
        "Condense, merge, or cut only. Do not add scenes. Return the complete trimmed Fountain.";

    public static string CastInstruction(string? locationHints) =>
        "Files attached: screenplay.fountain is the source of truth; book_full.txt (if present) is look detail only.\n" +
        "Do not add plot from the book. Return JSON only (schema_version cast_seeds.v1, " +
        "character_seed_tokens, location_seed_tokens).\n" +
        (string.IsNullOrWhiteSpace(locationHints)
            ? ""
            : "KNOWN PLACES FROM HEADINGS (cover each; you may unify synonyms into one Loc_*):\n" +
              locationHints.TrimEnd() + "\n");

    public sealed record Deps(
        XaiResponsesClient Responses,
        BookTextRegistryService? Registry,
        IBookFileSessionFactory? BookSessions,
        bool UseFakes);

    /// <summary>Enrich: book + full-length screenplay file_ids.</summary>
    public static Task<string?> TryCompleteAsync(
        Deps deps,
        string projectId,
        string projectDir,
        string screenplay,
        string? bookText,
        string systemPrompt,
        string model,
        Action<string>? onProgress,
        CancellationToken ct) =>
        TryCompleteAsync(
            deps, projectId, projectDir, screenplay, bookText, systemPrompt, EnrichInstruction, model,
            onProgress, ct, attachBook: true);

    /// <summary>
    /// Upload-or-reuse file_ids, then run Responses. Returns null when this path is unavailable
    /// (caller falls back to inlined chat).
    /// </summary>
    public static async Task<string?> TryCompleteAsync(
        Deps deps,
        string projectId,
        string projectDir,
        string? screenplay,
        string? bookText,
        string systemPrompt,
        string instruction,
        string model,
        Action<string>? onProgress,
        CancellationToken ct,
        bool attachBook = false,
        bool requireScreenplay = true,
        string screenplayKind = ProjectXaiArtifactFiles.KindScreenplayMax,
        string screenplayFilename = "screenplay.max.fountain",
        string label = "xAI Files")
    {
        if (deps.UseFakes) return null;
        if (deps.Responses is null || !XaiResponsesClient.IsConfigured) return null;
        if (!LooksXai(model)) return null;

        var fileIds = new List<string>();

        if (attachBook && !string.IsNullOrWhiteSpace(bookText))
        {
            var bookId = await ResolveBookIdAsync(deps, projectId, projectDir, bookText, ct).ConfigureAwait(false);
            var bookFileId = await EnsureBookFileIdAsync(
                deps, projectDir, bookId, bookText, model, onProgress, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bookFileId))
                fileIds.Add(bookFileId);
        }

        if (!string.IsNullOrWhiteSpace(screenplay))
        {
            var spFileId = await EnsureProjectFileAsync(
                deps.Responses, projectDir, screenplayKind, screenplay, screenplayFilename, onProgress, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(spFileId))
            {
                if (requireScreenplay) return null;
            }
            else
                fileIds.Add(spFileId);
        }
        else if (requireScreenplay)
            return null;

        if (fileIds.Count == 0) return null;

        onProgress?.Invoke($"{label} via file_id ({fileIds.Count} file(s), no body resend)…");

        var result = await deps.Responses.CompleteWithFilesAndSystemAsync(
            model, fileIds, systemPrompt, instruction, ct, temperature: 0.2).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.UsageJson))
            onProgress?.Invoke("xAI usage: " + result.UsageJson);
        if (!string.IsNullOrWhiteSpace(result.ResponseId))
            onProgress?.Invoke("xAI response_id=" + result.ResponseId);

        return result.OutputText;
    }

    static async Task<string?> ResolveBookIdAsync(
        Deps deps, string projectId, string projectDir, string? bookText, CancellationToken ct)
    {
        var fromManifest = (await ProjectStage1ConvertManifest.TryReadAsync(projectDir, ct).ConfigureAwait(false))?.BookId;
        if (!string.IsNullOrWhiteSpace(fromManifest)) return fromManifest;
        if (deps.Registry is null) return null;
        var fromAccess = await deps.Registry.FindBookIdForProjectAsync(projectId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromAccess)) return fromAccess;
        if (string.IsNullOrWhiteSpace(bookText)) return null;
        try
        {
            var identity = await deps.Registry.RegisterAsync(bookText, userId: "local", projectId, "Private", ct)
                .ConfigureAwait(false);
            return identity.BookId;
        }
        catch
        {
            return null;
        }
    }

    static async Task<string?> EnsureBookFileIdAsync(
        Deps deps,
        string projectDir,
        string? bookId,
        string? bookText,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(bookId) && deps.Registry is not null)
        {
            var existing = await deps.Registry.GetProviderFileAsync(bookId, XaiBookFileSession.ProviderName, ct)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (existing is not null &&
                !string.IsNullOrWhiteSpace(existing.FileId) &&
                (existing.ExpiresAtUnix is null || existing.ExpiresAtUnix > now + 3600))
            {
                onProgress?.Invoke($"Reusing book file_id for {bookId} (no re-upload).");
                return existing.FileId;
            }
        }

        if (string.IsNullOrWhiteSpace(bookText))
            return null;

        if (!string.IsNullOrWhiteSpace(bookId) && deps.BookSessions is not null)
        {
            try
            {
                var session = await deps.BookSessions.TryCreateAsync(bookId, bookText, model, ct)
                    .ConfigureAwait(false);
                if (session is { IsAvailable: true })
                {
                    await session.EnsureUploadedAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(session.FileId))
                    {
                        onProgress?.Invoke($"Book uploaded / reused as file_id={session.FileId}.");
                        return session.FileId;
                    }
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke("Book file session failed: " + ex.Message);
            }
        }

        return await EnsureProjectFileAsync(
            deps.Responses,
            projectDir,
            ProjectXaiArtifactFiles.KindBookFull,
            bookText,
            "book_full.txt",
            onProgress,
            ct).ConfigureAwait(false);
    }

    static async Task<string?> EnsureProjectFileAsync(
        XaiResponsesClient responses,
        string projectDir,
        string kind,
        string text,
        string filename,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var sha = ProjectXaiArtifactFiles.Sha256Hex(text);
        if (ProjectXaiArtifactFiles.TryGetReusable(projectDir, kind, sha, out var hit) && hit is not null)
        {
            onProgress?.Invoke($"Reusing {kind} file_id={hit.FileId} (same SHA, no re-upload).");
            return hit.FileId;
        }

        onProgress?.Invoke($"Uploading {filename} to xAI Files…");
        var bytes = Encoding.UTF8.GetBytes(text);
        var upload = await responses.UploadBookBytesAsync(bytes, filename, ct: ct).ConfigureAwait(false);
        ProjectXaiArtifactFiles.Upsert(projectDir, new ProjectXaiArtifactFiles.Entry
        {
            Kind = kind,
            Sha256 = sha,
            FileId = upload.FileId,
            ExpiresAtUnix = upload.ExpiresAtUnixSeconds,
            Bytes = bytes.Length,
            Filename = filename,
        });
        onProgress?.Invoke($"Saved {kind} file_id={upload.FileId}.");
        return upload.FileId;
    }

    static bool LooksXai(string? modelId)
    {
        var entry = SupportedModelCatalog.Find(modelId);
        var id = (modelId ?? "").Trim().ToLowerInvariant();
        return entry?.Provider == ModelProviderFamily.Xai
               || id.Contains("grok", StringComparison.Ordinal)
               || (entry?.ApiBase?.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

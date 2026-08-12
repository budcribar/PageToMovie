using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SkiaSharp;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Native character portrait design: Grok image gen/edit, lock/unlock refs.
/// Character portrait variants + lock/unlock to character_*_ref.png.
/// </summary>
public sealed class CharacterDesignService
{
    private readonly ProjectStore _projects;
    private readonly IImageClient _images;
    private readonly IVisionClient _vision;
    private readonly CostReportService _costs;
    private readonly CastVisualLiteralizeService _literalize;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<CharacterDesignService> _log;
    private const string DescriptionKey = "description";
    private readonly IUserContext? _user;

    public CharacterDesignService(
        ProjectStore projects,
        IImageClient images,
        IVisionClient vision,
        CostReportService costs,
        CastVisualLiteralizeService literalize,
        IOptions<PageToMovieOptions> opts,
        ILogger<CharacterDesignService> log,
        IUserContext? user = null)
    {
        _projects = projects;
        _images = images;
        _vision = vision;
        _costs = costs;
        _literalize = literalize;
        _opts = opts.Value;
        _log = log;
        _user = user;
    }

    private string? CurrentUserId =>
        string.IsNullOrWhiteSpace(_user?.UserId) ? null : _user!.UserId;

    /// <param name="n">Variant count. Pass 0 (default) for auto: 1 if locked, else 3.</param>
    /// <param name="seedOptions">Flexible seed policy (auto / preferred / book / explicit multi-select).</param>
    public async Task<CharacterDesignResult> GenerateVariantsAsync(
        string projectId,
        string charKey,
        int n = 0,
        PageToMovie.Core.Models.StartCharacterVariantsRequest? seedOptions = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_images.IsConfigured)
            throw new InvalidOperationException("XAI_API_KEY is not set (required for portrait generation).");

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no portrait variants.");

        var wardrobeLockKey = ProjectStore.GetWardrobeLockKey(seeds);
        var wardrobeLock = !string.IsNullOrWhiteSpace(wardrobeLockKey)
            ? _projects.GetWardrobeLock(projectId, wardrobeLockKey!)
            : null;

        var charDir = _projects.GetCharactersDir(projectId);
        Directory.CreateDirectory(charDir);

        var opts = seedOptions ?? new PageToMovie.Core.Models.StartCharacterVariantsRequest
        {
            ProjectId = projectId,
            CharKey = charKey,
        };
        var imageModel = ProjectModelSelection.RequireImage(
            await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false),
            "Character portrait generation");
        var imageProvider = await GetConfigStringAsync(projectId, "image_provider", _opts.ImageProvider, ct)
            .ConfigureAwait(false);
        var providerId = ImageApiLimits.ResolveProvider(imageProvider, imageModel);
        // Catalog maxReferenceImages only (ClampMaxRefs → ImageApiLimits fail-fast).
        var maxRefs = ImageApiLimits.ClampMaxRefs(opts.MaxRefs, imageProvider, imageModel);
        var maxBook = Math.Clamp(opts.MaxBookHints < 0 ? Math.Max(0, maxRefs - 1) : opts.MaxBookHints, 0, maxRefs);

        // Shared uniform lock: reuse (or generate once) a costume-only reference plate so
        // every character in this wardrobe group renders the identical coat/hat/badge design
        // instead of each independently re-imagining "civil hat"/"badge" per generate call.
        string? costumeRefPath = null;
        if (wardrobeLock is not null && wardrobeLockKey is not null)
        {
            costumeRefPath = await EnsureWardrobeReferenceAsync(
                projectId, wardrobeLockKey, wardrobeLock.Value, imageModel, onProgress, ct)
                .ConfigureAwait(false);
        }

        var preferredPath = ResolvePreferredImagePath(projectId, charKey, charDir);
        var preferredName = preferredPath is null ? "" : Path.GetFileName(preferredPath);
        var alreadyLocked = preferredPath is not null &&
            ProjectStore.CharacterRefFileCandidates(charKey)
                .Any(c => string.Equals(c, preferredName, StringComparison.OrdinalIgnoreCase));
        if (n <= 0)
            n = opts.Count > 0 ? opts.Count : (alreadyLocked ? 1 : 3);
        n = Math.Clamp(n, 1, 6);
        // Iterative face tweak: one new look, keep the current lock as a sibling to pick from.
        LookTweakSlots.Pair? tweakSlots = null;
        if (opts.IterativeEdit)
        {
            n = 1;
            tweakSlots = LookTweakSlots.Allocate(
                charDir,
                i => $"{charKey.ToLowerInvariant()}_variant_0{i}.png",
                preferredPath);
        }

        var allBookRefs = ResolveBookRefPaths(projectDir, seeds, maxRefs: 12);
        var editRefs = ResolveEditRefs(
            projectId, charKey, charDir, preferredPath, allBookRefs, opts, maxRefs, maxBook, onProgress);

        // Wardrobe-locked characters: face/build from text + wardrobe from the shared costume
        // plate only — never also feed in this character's own previous portrait or candidate
        // variants. Telling the model to "match face but ignore wardrobe" from that photo is a
        // soft instruction competing against real pixels of an outfit that (pre-lock) may not
        // even match the shared design; simplest and most reliable is to not send that
        // competing signal at all. Book reference art (original illustrations, not
        // previously-generated candidates) is unaffected.
        //
        // No seed-mode exception here: the Characters UI's regenerate button defaults to
        // SeedMode=explicit whenever anything is pre-selected (Characters.razor
        // StartRegenerateAsync) — which is always true for an already-locked character (the
        // lock + last variant are pre-checked). So "explicit" is the NORMAL path for a routine
        // regenerate here, not a rare deliberate power-user override; respecting it would mean
        // this fix never actually applies to real usage.
        if (costumeRefPath is not null)
        {
            // Allowlist match (locked-ref name or "_variant_N") — NOT a "starts with charKey_"
            // prefix match. Book plates are legitimate reference art (original book scan pages,
            // e.g. "page_003_render.png", or the legacy "{charkey}_bookref_N" convention used
            // by DeleteImage) and must never be stripped here — only this character's own
            // previously-generated candidate portraits are in scope.
            var ownRefNames = new HashSet<string>(
                ProjectStore.CharacterRefFileCandidates(charKey), StringComparer.OrdinalIgnoreCase);
            var variantPattern = new System.Text.RegularExpressions.Regex(
                $@"^{System.Text.RegularExpressions.Regex.Escape(charKey.ToLowerInvariant())}_variant_\d+\.",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var removed = editRefs.RemoveAll(p =>
            {
                var name = Path.GetFileName(p);
                return ownRefNames.Contains(name) || variantPattern.IsMatch(name);
            });
            if (removed > 0)
                onProgress?.Invoke(
                    $"Wardrobe lock active — dropping {removed} of this character's own previous " +
                    "picture(s) from refs; using text description + shared costume plate only.");
        }

        // Keep operator-selected seed order. Do not promote preferred/locked over book plates
        // when explicit SeedOrderKeys were sent (Characters UI ranks Book / Preferred / Option tiles).

        onProgress?.Invoke(
            $"Seed mode={NormalizeSeedMode(opts.SeedMode)} · refs={editRefs.Count}/{maxRefs} · variants={n}" +
            (editRefs.Count > 0
                ? $" · files={string.Join(",", editRefs.Select(Path.GetFileName))}"
                : ""));

        // Resolve text for this generate, then AI-scrub (base look + literal) via prompt —
        // no special-case regex lists for pajamas / nicknames / etc.
        var descForGen = opts.DescriptionOverride;
        var visForGen = opts.VisualLockOverride;
        if (descForGen is null && seeds.TryGetProperty(DescriptionKey, out var d0))
            descForGen = d0.GetString();
        if (visForGen is null && seeds.TryGetProperty("visual_lock", out var v0))
            visForGen = v0.GetString();

        string? planningModel = null;
        try
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            planningModel = ProjectModelSelection.RequirePlanning(cfg, "Character design look scrub");
        }
        catch (InvalidOperationException)
        {
            throw;
        }

        try
        {
            var (dScrub, vScrub, usedAi) = await _literalize.ScrubLookFieldsAsync(
                charKey,
                description: descForGen,
                visualLock: visForGen,
                model: planningModel!,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (usedAi)
            {
                descForGen = dScrub;
                visForGen = vScrub;
                onProgress?.Invoke("AI scrub applied to look text for this generate");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Look AI scrub before portrait gen failed — using raw text");
        }

        // Optional persist of (scrubbed) description / visual_lock from Characters UI
        if (opts.PersistDescription &&
            (opts.DescriptionOverride is not null || opts.VisualLockOverride is not null ||
             descForGen is not null || visForGen is not null))
        {
            _projects.UpdateCharacterSeedText(
                projectId,
                charKey,
                description: descForGen,
                visualLock: visForGen);
            seeds = _projects.GetCharacterSeed(projectId, charKey) ?? seeds;
            onProgress?.Invoke("Saved scrubbed description / visual lock to cast seeds");
        }

        var hasImageHints = editRefs.Count > 0 || costumeRefPath is not null;
        var projectStyle = ReadProjectRenderStyleLock(projectDir);
        var wardrobeDescription = wardrobeLock is { } wl && wl.TryGetProperty(DescriptionKey, out var wd)
            ? wd.GetString()
            : null;
        var (prompt, illustratedMedium) = BuildDesignPrompt(
            charKey,
            seeds,
            hasImageHints,
            descriptionOverride: descForGen,
            visualLockOverride: visForGen,
            projectRenderStyleLock: projectStyle,
            wardrobeLockDescription: wardrobeDescription,
            hasIdentityRefs: editRefs.Count > 0,
            hasCostumeRef: costumeRefPath is not null,
            hasBookIllustrations: ProjectHasBookIllustrations(projectDir));

        onProgress?.Invoke(
            $"design prompt ready ({prompt.Length} chars) · image_provider={ImageApiLimits.ResolveProvider(imageProvider, imageModel)} max_refs={maxRefs}");
        IReadOnlyList<byte[]> blobs;
        var mode = "text_only";
        string? editError = null;

        // If preferred lives in variant_01, snapshot it so we can overwrite variant slots safely
        string? preferredSnapshot = null;
        try
        {
            if (preferredPath is not null &&
                Path.GetFileName(preferredPath).Contains("_variant_", StringComparison.OrdinalIgnoreCase))
            {
                preferredSnapshot = Path.Combine(
                    charDir, $"{charKey.ToLowerInvariant()}_preferred_snap.png");
                File.Copy(preferredPath, preferredSnapshot, overwrite: true);
                var i = editRefs.FindIndex(p =>
                    string.Equals(p, preferredPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) editRefs[i] = preferredSnapshot;
            }

            if (hasImageHints)
            {
                var identityLabel = editRefs.Count > 0 ? Path.GetFileName(editRefs[0]) : "(none — costume ref only)";
                onProgress?.Invoke(
                    $"Grok image edit with {editRefs.Count} identity hint(s)" +
                    (costumeRefPath is not null
                        ? $" + shared costume ref ({Path.GetFileName(costumeRefPath)})"
                        : "") +
                    $" [primary={identityLabel}]: " +
                    string.Join(", ", editRefs.Select(Path.GetFileName)));
                try
                {
                    var primary = editRefs.Take(maxRefs).ToList();
                    blobs = await _images.EditVariantsAsync(
                        prompt,
                        primary,
                        n,
                        aspectRatio: "1:1",
                        model: imageModel,
                        maxRefs: maxRefs,
                        costumeRefPath: costumeRefPath,
                        illustratedMedium: illustratedMedium,
                        onProgress: onProgress,
                        ct: ct);
                    mode = alreadyLocked
                        ? (primary.Count > 1 ? "preferred_multi" : "preferred_locked")
                        : (primary.Count > 1 ? "preferred_or_book_multi" : "preferred_or_book");
                    if (costumeRefPath is not null) mode += "_wardrobe_locked";
                }
                catch (Exception ex)
                {
                    editError = ex.Message;
                    // User picked image seeds — never silently invent a different dog from text
                    // Retry once with preferred-only if multi-ref failed
                    if (preferredPath is not null && File.Exists(preferredPath) && editRefs.Count > 1)
                    {
                        onProgress?.Invoke(
                            $"Multi-ref edit failed ({ex.Message}); retry preferred-only…");
                        try
                        {
                            blobs = await _images.EditVariantsAsync(
                                prompt,
                                new[] { preferredPath },
                                n,
                                aspectRatio: "1:1",
                                model: imageModel,
                                maxRefs: costumeRefPath is not null ? 2 : 1,
                                costumeRefPath: costumeRefPath,
                                illustratedMedium: illustratedMedium,
                                onProgress: onProgress,
                                ct: ct);
                            mode = "preferred_only_retry";
                        }
                        catch (Exception ex2)
                        {
                            throw new InvalidOperationException(
                                "Image-guided edit failed (multi-ref and preferred-only). " +
                                "Not falling back to text-only — that invents a different character. " +
                                $"Last error: {ex2.Message}", ex2);
                        }
                    }
                    else if (costumeRefPath is not null && editRefs.Count == 0)
                    {
                        // Only the shared costume ref was attached (no face ref yet for this
                        // character) — nothing smaller to retry with.
                        throw new InvalidOperationException(
                            "Image-guided edit failed using the shared wardrobe reference. " +
                            "Not falling back to text-only — that would break uniform consistency " +
                            $"with the rest of the group. Error: {ex.Message}", ex);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Image-guided edit failed. Not falling back to text-only " +
                            $"(would ignore your selected seeds). Error: {ex.Message}", ex);
                    }
                }
            }
            else
            {
                onProgress?.Invoke("Text-only generation (no preferred image and no book plates)");
                blobs = await _images.GenerateVariantsAsync(
                    prompt, n, aspectRatio: "1:1", model: imageModel, ct: ct);
                mode = "text_only";
            }

            var paths = new List<string>();
            for (var i = 0; i < blobs.Count && i < n; i++)
            {
                var idx = tweakSlots is { } slots ? slots.Next : i + 1;
                var fileName = $"{charKey.ToLowerInvariant()}_variant_0{idx}.png";
                var full = Path.Combine(charDir, fileName);
                await File.WriteAllBytesAsync(full, blobs[i], ct);
                paths.Add(full);
                onProgress?.Invoke($"saved variant {idx}/{n} → {fileName}");

                try
                {
                    await _costs.RecordImageGenerationAsync(
                        projectId, 1, imageModel, quality: true,
                        character: charKey, userId: CurrentUserId, ct: ct);
                }
                catch (Exception costEx)
                {
                    _log.LogWarning(costEx, "Could not record image cost");
                }
            }

            if (paths.Count < 1)
                throw new InvalidOperationException($"No variants generated for {charKey}");

            if (opts.IterativeEdit && tweakSlots is { } kept)
                onProgress?.Invoke($"New look is #{kept.Next} — current lock is #{kept.Previous}. Pick one.");

            return new CharacterDesignResult
            {
                CharKey = charKey,
                Mode = mode,
                Paths = paths,
                BookRefs = editRefs.Select(Path.GetFileName).Where(s => s is not null).Cast<string>().ToList(),
                EditError = editError,
                LockedAsPreferred = false,
                PreviousVariantIndex = tweakSlots?.Previous,
                NewVariantIndex = tweakSlots?.Next,
            };
        }
        finally
        {
            if (preferredSnapshot is not null)
            {
                try { File.Delete(preferredSnapshot); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Build ordered image seeds for Grok from flexible policy.
    /// Preferred first (when included), then book / explicit selections, capped at maxRefs.
    /// </summary>
    private List<string> ResolveEditRefs(
        string projectId,
        string charKey,
        string charDir,
        string? preferredPath,
        List<string> allBookRefs,
        PageToMovie.Core.Models.StartCharacterVariantsRequest opts,
        int maxRefs,
        int maxBook,
        Action<string>? onProgress)
    {
        var mode = NormalizeSeedMode(opts.SeedMode);
        var editRefs = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (new FileInfo(path).Length < 64) return;
            if (editRefs.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
            if (editRefs.Count >= maxRefs) return;
            editRefs.Add(path);
        }

        switch (mode)
        {
            case "none":
                onProgress?.Invoke("Seed mode=none → text-only (no image refs)");
                return editRefs;

            case "preferred_only":
                if (opts.IncludePreferred)
                    Add(preferredPath);
                onProgress?.Invoke(
                    preferredPath is null
                        ? "Preferred-only mode but no preferred image"
                        : $"Preferred-only: {Path.GetFileName(preferredPath)}");
                return editRefs;

            case "explicit":
                // Prefer ordered keys from UI (rank 1..N). Fall back to separate lists.
                if (opts.SeedOrderKeys is { Count: > 0 })
                {
                    foreach (var raw in opts.SeedOrderKeys)
                    {
                        if (editRefs.Count >= maxRefs) break;
                        var key = (raw ?? "").Trim().ToLowerInvariant();
                        if (key is "p" or "pref" or "preferred")
                        {
                            Add(preferredPath);
                            continue;
                        }
                        if (key.Length >= 2 && key[0] == 'v' && int.TryParse(key[1..], out var vi) &&
                            vi is >= 1 and <= 3)
                        {
                            Add(Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{vi}.png"));
                            continue;
                        }
                        if (key.Length >= 2 && key[0] == 'b' && int.TryParse(key[1..], out var bi) &&
                            bi >= 0 && bi < allBookRefs.Count)
                        {
                            Add(allBookRefs[bi]);
                        }
                    }
                }
                else
                {
                    if (opts.IncludeLockedRef || opts.IncludePreferred)
                        Add(preferredPath);
                    foreach (var vi in opts.VariantIndices.Distinct())
                    {
                        if (vi is < 1 or > 3) continue;
                        Add(Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{vi}.png"));
                    }
                    foreach (var bi in opts.BookRefIndices.Distinct())
                    {
                        if (bi < 0 || bi >= allBookRefs.Count) continue;
                        Add(allBookRefs[bi]);
                    }
                }
                onProgress?.Invoke(
                    editRefs.Count == 0
                        ? "Explicit mode: no valid selections — will text-only"
                        : $"Explicit seeds ({editRefs.Count}/{maxRefs}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;

            case "book_hints":
                if (opts.IncludePreferred)
                    Add(preferredPath);
                foreach (var br in allBookRefs.Take(maxRefs))
                    Add(br);
                onProgress?.Invoke(
                    allBookRefs.Count == 0
                        ? "Book-hints mode: no plates attached for character"
                        : $"Book-hints + preferred ({editRefs.Count}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;

            default: // auto
                if (opts.IncludePreferred)
                    Add(preferredPath);
                foreach (var br in allBookRefs.Take(maxBook))
                    Add(br);
                onProgress?.Invoke(
                    editRefs.Count == 0
                        ? "Auto seeds: none — description only"
                        : $"Auto seeds ({editRefs.Count}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;
        }
    }

    private static string NormalizeSeedMode(string? mode)
    {
        mode = (mode ?? "auto").Trim().ToLowerInvariant().Replace('-', '_');
        return mode switch
        {
            "preferred" or "preferred_only" or "pref" => "preferred_only",
            "book" or "book_hints" or "search_book" => "book_hints",
            "explicit" or "custom" or "manual" => "explicit",
            "none" or "text" or "text_only" => "none",
            _ => "auto",
        };
    }

    public async Task<string> LockVariantAsync(
        string projectId,
        string charKey,
        int variantIndex,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        if (variantIndex is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(variantIndex), "variant index must be 1..3");
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var variantPath = Path.Combine(projectDir, "assets", "characters", fileName);
        if (!File.Exists(variantPath))
            throw new InvalidOperationException($"Variant not found: {fileName}");
        return await LockFromPathAsync(projectId, charKey, variantPath, allowStyleOverride, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rank existing variant_01..N with vision against description/visual_lock, lock the winner.
    /// Operator can re-lock another variant later from the Cast UI.
    /// </summary>
    public async Task<(int VariantIndex, string RefPath)> AutoLockBestVariantAsync(
        string projectId,
        string charKey,
        int maxVariants = 3,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        var desc = seeds.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var vlock = seeds.TryGetProperty("visual_lock", out var v) ? v.GetString() ?? "" : "";

        var found = new List<(int Index, string Path)>();
        for (var i = 1; i <= Math.Clamp(maxVariants, 1, 3); i++)
        {
            var path = Path.Combine(projectDir, "assets", "characters",
                $"{charKey.ToLowerInvariant()}_variant_0{i}.png");
            if (File.Exists(path) && new FileInfo(path).Length >= 64)
                found.Add((i, path));
        }
        if (found.Count == 0)
            throw new InvalidOperationException($"No portrait variants on disk for {charKey}");

        onProgress?.Invoke($"AI picking best look for {charKey} ({found.Count} options)…");
        var best = await LookVariantPicker.PickBestIndexAsync(
            _vision, _log, "character portrait", charKey, desc, vlock, found, ct).ConfigureAwait(false);
        onProgress?.Invoke($"Auto-locking variant {best} for {charKey}");
        // allowStyleOverride: batch auto-lock should not hard-fail the whole plan on style gate
        var refPath = await LockVariantAsync(projectId, charKey, best, allowStyleOverride: true, ct)
            .ConfigureAwait(false);
        return (best, refPath);
    }

    public Task<string> LockBookRefAsync(
        string projectId,
        string charKey,
        int bookIndex,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        var path = _projects.ResolveCharacterBookRefPath(projectId, charKey, bookIndex)
            ?? throw new InvalidOperationException($"Book ref {bookIndex} not found for {charKey}");
        return LockFromPathAsync(projectId, charKey, path, allowStyleOverride, ct);
    }

    public async Task<string> LockFromPathAsync(
        string projectId,
        string charKey,
        string sourcePath,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no reference image to lock.");

        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Image not found: {sourcePath}");

        await EnsurePortraitStyleAllowedAsync(projectId, charKey, sourcePath, allowStyleOverride, ct).ConfigureAwait(false);

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var refName = ProjectStore.CharacterRefFileName(charKey);
        var dest = Path.Combine(projectDir, "assets", "characters", refName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // Always store real PNG bytes under *_ref.png (JPEG/WebP uploaded as .png break browsers).
        WritePortraitPng(sourcePath, dest);

        FinalizeLock(projectId, charKey, dest, $"Locked reference from {Path.GetFileName(sourcePath)}");
        return dest;
    }

    /// <summary>
    /// Operator upload: save image bytes as the locked character ref (preferred look for video).
    /// Accepts png/jpg/webp/gif; stored as the canonical <c>*_ref.png</c> name (bytes as-is).
    /// </summary>
    public async Task<string> LockFromUploadAsync(
        string projectId,
        string charKey,
        Stream content,
        string? originalFileName = null,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no reference image to lock.");

        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Empty upload stream");

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var charDir = _projects.GetCharactersDir(projectId);
        Directory.CreateDirectory(charDir);
        var staging = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_upload_staging_{Guid.NewGuid():N}.bin");

        try
        {
            await using (var fs = File.Create(staging))
            {
                await content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (new FileInfo(staging).Length < 64)
                throw new InvalidOperationException("Uploaded image is empty or too small.");

            await EnsurePortraitStyleAllowedAsync(projectId, charKey, staging, allowStyleOverride, ct).ConfigureAwait(false);

            var refName = ProjectStore.CharacterRefFileName(charKey);
            var dest = Path.Combine(charDir, refName);
            WritePortraitPng(staging, dest);

            var label = string.IsNullOrWhiteSpace(originalFileName)
                ? "operator upload"
                : Path.GetFileName(originalFileName);
            FinalizeLock(projectId, charKey, dest, $"Locked reference from upload ({label})");
            return dest;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Vision gate: refuse to lock a sketch/illustration when the project is photoreal
    /// (or a photo stock plate when the project is picture-book). Fail closed on mismatch.
    /// </summary>
    internal async Task EnsurePortraitStyleAllowedAsync(
        string projectId,
        string charKey,
        string imagePath,
        bool allowStyleOverride = false,
        CancellationToken ct = default)
    {
        if (!_opts.RequirePortraitStyleGate)
        {
            _log.LogWarning("Portrait style gate disabled (RequirePortraitStyleGate=false)");
            return;
        }

        // User override: the creator's intent wins over the style classifier. This is how a photoreal
        // character can live in an animated film (or vice versa) — the user chose this look on purpose,
        // so we skip the style verdict and lock it. The classifier is advisory, not a hard wall.
        if (allowStyleOverride)
        {
            _log.LogInformation(
                "Portrait style gate OVERRIDDEN for {CharKey} — locking regardless of project medium (intentional mixed-media, user choice).",
                charKey);
            return;
        }

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var styleLock = ReadProjectRenderStyleLock(projectDir);
        // No project medium → nothing to enforce (ambiguous mixed projects)
        if (string.IsNullOrWhiteSpace(styleLock))
            return;

        // Need a clear medium preference; pure free-form style text still runs the gate.
        var wantIllustrated = PrefersIllustratedPortraitStyle(
            styleLock, hasImageHints: false, isAnimal: false);
        // PrefersIllustrated with empty photoreal cues returns false only when photoreal cues
        // are present or default-to-photoreal; still detect cues for expected label.
        var hasPhotoCues = RegexContains(styleLock,
            @"\b(photoreal|photo-?real|live[- ]?action|cinematic|film photography|" +
            @"period drama|naturalistic|photographic)\b");
        var hasIllustCues = RegexContains(styleLock,
            @"\b(picture[- ]?book|illustrated|illustration|cartoon|painted|anime|comic)\b");
        if (!hasPhotoCues && !hasIllustCues && !wantIllustrated)
            return; // style present but medium ambiguous — do not block lock

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: portrait image file is missing.");

        // Vision clients only attach known extensions — upload staging uses ".bin".
        string? tempForVision = null;
        var visionPath = imagePath;
        try
        {
            visionPath = MaterializeImagePathForVision(imagePath, out tempForVision);
            if (new FileInfo(visionPath).Length < 64)
                throw new InvalidOperationException(
                    $"Cannot lock {charKey}: portrait image is empty or unreadable.");

            var expected = hasPhotoCues && !wantIllustrated ? "photoreal" : "illustration";
            var prompt =
                $"Project style lock: {styleLock.Trim()}\n" +
                $"Expected medium for this project: {expected}\n\n" +
                "An image is attached to this message. You MUST inspect that image.\n" +
                "Classify the image medium:\n" +
                "- photoreal = live-action photography / cinematic photo of a real person or animal " +
                "(natural skin/fur texture, photographic lighting)\n" +
                "- illustration = drawn/painted/cartoon/picture-book art\n" +
                "- sketch = pencil/line drawing only\n" +
                "- other = only if truly unreadable (blur, blank, non-portrait)\n" +
                "pass=true ONLY if the image medium matches Expected.\n" +
                "For expected=photoreal: FAIL sketch, illustration, cartoon, pencil drawing.\n" +
                "For expected=illustration: FAIL pure photoreal stock photography.\n" +
                "Never claim the image is missing if an attachment is present.\n\n" +
                "Reply with JSON only:\n" +
                "{\"pass\":true|false,\"medium\":\"photoreal|illustration|sketch|other\",\"reason\":\"short\"}\n";

            string? visionModel = null;
            try
            {
                var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
                visionModel = ProjectModelSelection.RequireVision(cfg, "Portrait style gate");
            }
            catch (InvalidOperationException)
            {
                throw;
            }

            var gate = await RunPortraitStyleGateAsync(
                prompt, visionPath, visionModel, detail: "low", ct).ConfigureAwait(false);

            // "no image attached" / medium=other is usually a transport/extension bug, not style.
            if (IsVisionBlindGate(gate))
            {
                _log.LogWarning(
                    "Portrait style gate appeared blind for {CharKey} (medium={Medium} reason={Reason}); retry high detail",
                    charKey, gate.Medium, gate.Reason);
                gate = await RunPortraitStyleGateAsync(
                    prompt, visionPath, visionModel, detail: "high", ct).ConfigureAwait(false);
            }

            if (IsVisionBlindGate(gate))
            {
                throw new InvalidOperationException(
                    $"Cannot lock {charKey}: style check could not read the portrait image " +
                    $"({gate.Medium}: {gate.Reason}). Try again, or re-generate the look.");
            }

            if (!gate.Pass)
            {
                throw new InvalidOperationException(
                    $"Cannot lock {charKey}: this look does not match the project style " +
                    $"(got {gate.Medium}, expected {expected}). {gate.Reason} " +
                    "Generate a new look that matches the project style, then choose it again.");
            }

            // Extra hard reject: never lock sketch on photoreal projects even if model said pass.
            if (expected == "photoreal" &&
                gate.Medium is "sketch" or "illustration")
            {
                throw new InvalidOperationException(
                    $"Cannot lock {charKey}: this look is {gate.Medium}, but the project is live-action / photoreal. " +
                    $"{gate.Reason}");
            }

            _log.LogInformation(
                "Portrait style gate OK for {CharKey}: medium={Medium} expected={Expected}",
                charKey, gate.Medium, expected);
        }
        finally
        {
            if (tempForVision is not null)
            {
                try { File.Delete(tempForVision); } catch { /* ignore */ }
            }
        }
    }

    private const string PortraitStyleGatePromptVersion = "v1";

    /// <summary>
    /// Runs through <see cref="ModelExecution.ValidatedModelOperation{TInput,TRaw,TResult}"/> — same
    /// contract as the beat classifiers, via the <c>ModelBacked</c> single-shot-directive pattern
    /// (<see cref="ModelBacked.PortraitStyleGateOperation"/>), not the batched-coverage one those use
    /// (wrong shape for a single-image single-verdict call). Gains a corrective re-ask when the model
    /// returns malformed JSON or an unrecognized medium (previously an immediate hard failure), plus
    /// provenance/reproducibility tracing.
    /// </summary>
    private async Task<PortraitStyleGateResult> RunPortraitStyleGateAsync(
        string prompt,
        string visionPath,
        string? visionModel,
        string detail,
        CancellationToken ct)
    {
        var model = ProjectModelSelection.RequireExplicit(visionModel, ModelCapability.Vision, "Portrait style gate");
        var pipeline = new ModelExecution.ValidatedModelOperation<ModelBacked.PortraitStyleGateInput, string, PortraitStyleGateResult>(
            new ModelBacked.PortraitStyleGateOperation(_vision, "portrait_style_gate", PortraitStyleGatePromptVersion),
            new ModelBacked.PortraitStyleGateResponseParser(),
            new ModelBacked.PortraitStyleGateValidator(),
            new ModelBacked.DirectiveTerminalFallback<ModelBacked.PortraitStyleGateInput, PortraitStyleGateResult>(),
            new ModelExecution.ModelOperationOptions
            {
                CorrectiveMaxAttempts = 1,
                // IVisionClient.CompleteWithImagesAsync already retries transient failures
                // internally (429/5xx/network, Retry-After-aware, backed off) — retrying again at
                // this outer layer would multiply attempts (up to 3x3x2=18 raw calls) instead of
                // adding resilience. Same reasoning as AiRetryPolicy.RunWithCoverageRetryAsync's
                // beat-classifier wiring.
                TransportMaxAttempts = 1,
            });

        ModelExecution.ValidatedModelResult<PortraitStyleGateResult> result;
        try
        {
            result = await pipeline.ExecuteAsync(
                new ModelBacked.PortraitStyleGateInput(prompt, visionPath, model, detail), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Cannot lock character: style check failed ({ex.Message}). Try again.", ex);
        }

        if (result.Value is null)
        {
            var detailMsg = result.Error
                ?? string.Join(" ", result.ValidationIssues.Select(i => i.Message));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detailMsg)
                    ? "Cannot lock character: style check returned an unreadable response. Try again."
                    : $"Cannot lock character: style check failed ({detailMsg}). Try again.");
        }
        return result.Value;
    }

    private static bool IsVisionBlindGate(PortraitStyleGateResult gate)
    {
        if (string.Equals(gate.Medium, "other", StringComparison.OrdinalIgnoreCase))
        {
            var r = gate.Reason ?? "";
            if (r.Length == 0) return true;
            if (RegexContains(r,
                    @"\b(no image|not attached|missing image|cannot see|can't see|no (photo|picture|portrait)|empty|blank|unreadable|not provided)\b"))
                return true;
        }
        var reason = gate.Reason ?? "";
        return RegexContains(reason,
            @"\b(no image attached|image not (attached|provided|present)|did not receive an image)\b");
    }

    /// <summary>
    /// Vision clients filter by extension; upload staging uses <c>.bin</c>. Sniff bytes and copy to a temp file with a real image extension when needed.
    /// </summary>
    public static string MaterializeImagePathForVision(string imagePath, out string? tempPathToDelete)
    {
        tempPathToDelete = null;
        var ext = Path.GetExtension(imagePath) ?? "";
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return imagePath;

        var sniffExt = SniffImageExtension(imagePath) ?? ".png";
        var temp = Path.Combine(
            Path.GetTempPath(),
            $"ptm-portrait-gate-{Guid.NewGuid():N}{sniffExt}");
        File.Copy(imagePath, temp, overwrite: true);
        tempPathToDelete = temp;
        return temp;
    }

    private static string? SniffImageExtension(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            var n = fs.Read(header);
            if (n >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ".jpg";
            if (n >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ".png";
            if (n >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                return ".gif";
            if (n >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ".webp";
        }
        catch { /* fall through */ }
        return null;
    }

    public static PortraitStyleGateResult? ParsePortraitStyleGateResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        text = CommonRegex.Replace(
            text, @"^```(?:json)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = CommonRegex.Replace(text, @"\s*```$", "").Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var pass = false;
            if (root.TryGetProperty("pass", out var p))
            {
                pass = p.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(p.GetString(), out var b) && b,
                    _ => false,
                };
            }
            var medium = root.TryGetProperty("medium", out var m)
                ? (m.GetString() ?? "other").Trim().ToLowerInvariant()
                : "other";
            if (medium is "photo" or "photographic" or "live-action" or "live_action"
                or "realistic" or "cinematic")
                medium = "photoreal";
            if (medium is "drawn" or "drawing" or "cartoon" or "picture-book" or "picture_book"
                or "illustrated" or "painting" or "painted")
                medium = "illustration";
            if (medium is "pencil" or "charcoal" or "line-art" or "lineart")
                medium = "sketch";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "").Trim() : "";
            return new PortraitStyleGateResult(pass, medium, reason);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForError(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>A class, not a struct — <see cref="ModelExecution.ValidatedModelOperation{TInput,TRaw,TResult}"/>
    /// requires <c>TResult : class</c>.</summary>
    public sealed record PortraitStyleGateResult(bool Pass, string Medium, string Reason);

    private void FinalizeLock(string projectId, string charKey, string destPath, string changeNote)
    {
        // Keep generated variants on disk so they stay available as reference tiles
        // for the next regenerate (preferred lock is a separate *_ref.png copy).
        _projects.UpdateCharacterSeedPlaceholder(projectId, charKey, ProjectStore.CharacterRefFileName(charKey));
        _projects.MarkCharacterChanged(projectId, charKey, changeNote);
        _projects.InvalidateReadCaches(projectId);
        if (!File.Exists(destPath) || new FileInfo(destPath).Length < 64)
            throw new InvalidOperationException(
                $"Locked look was not saved for {charKey}. Try uploading again.");
    }

    /// <summary>
    /// Decode any common photo format and write a real PNG to the canonical *_ref.png path.
    /// Storing JPEG bytes under a .png name makes locks "disappear" in the browser after reload.
    /// </summary>
    internal static void WritePortraitPng(string sourcePath, string destPngPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException("Portrait image file is missing.");

        Directory.CreateDirectory(Path.GetDirectoryName(destPngPath)!);

        using var bitmap = SKBitmap.Decode(sourcePath);
        if (bitmap is null)
        {
            // Last resort: raw copy (may already be PNG)
            File.Copy(sourcePath, destPngPath, overwrite: true);
            if (new FileInfo(destPngPath).Length < 64)
                throw new InvalidOperationException("Could not decode uploaded portrait image.");
            return;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        if (data is null || data.Size < 64)
            throw new InvalidOperationException("Could not encode portrait as PNG.");

        var tmp = destPngPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
                data.SaveTo(fs);
            File.Move(tmp, destPngPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* */ }
        }
    }

    /// <summary>
    /// Delete a character reference image: preferred lock, generated variant, or book plate.
    /// Book plates are also removed from cast seed design_reference_images.
    /// </summary>
    public void DeleteImage(string projectId, string charKey, string kind, int index = 0)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no image to delete.");

        var projectDir = _projects.GetProjectDir(projectId);
        var charDir = _projects.GetCharactersDir(projectId);
        var k = (kind ?? "").Trim().ToLowerInvariant();

        if (k is "preferred" or "p" or "ref" or "lock" or "locked")
        {
            foreach (var name in ProjectStore.CharacterRefFileCandidates(charKey))
            {
                var full = Path.Combine(charDir, name);
                try { if (File.Exists(full)) File.Delete(full); } catch { /* ignore */ }
            }
            _projects.UpdateCharacterSeedPlaceholder(projectId, charKey, "");
            _projects.MarkCharacterChanged(projectId, charKey, "Deleted preferred/locked picture");
            return;
        }

        if (k is "variant" or "v")
        {
            var i = Math.Clamp(index, 1, 9);
            var full = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{i}.png");
            if (!File.Exists(full))
                throw new InvalidOperationException($"Variant {i} not found.");
            File.Delete(full);
            _projects.MarkCharacterChanged(projectId, charKey, $"Deleted variant {i}");
            return;
        }

        if (k is "book" or "bookref" or "b")
        {
            // Seed paths are 0-based indices into design_reference_images
            _projects.RemoveCharacterBookRef(projectId, charKey, index);
            // Also delete common bookref filename if present
            var prefix = charKey.ToLowerInvariant() + "_bookref_";
            // index is 0-based in seeds; files are 1-based bookref_1
            var fileIdx = index + 1;
            if (Directory.Exists(charDir))
            {
                foreach (var fi in new DirectoryInfo(charDir).GetFiles($"{prefix}{fileIdx}.*"))
                {
                    try { fi.Delete(); } catch { /* ignore */ }
                }
            }
            _projects.MarkCharacterChanged(projectId, charKey, $"Deleted book picture {index}");
            return;
        }

        throw new InvalidOperationException($"Unknown image kind: {kind}");
    }

    /// <summary>
    /// Clear the official lock so video gen requires re-lock, but keep the image
    /// as variant_01 — the "best so far" seed for comparison / regenerate.
    /// Does not delete other variants.
    /// </summary>
    public bool Unlock(string projectId, string charKey)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey);
        if (seeds is null) return false;
        if (IsVoiceOnly(charKey, seeds.Value))
            throw new InvalidOperationException($"{charKey} is voice-only — nothing to unlock.");

        var projectDir = _projects.GetProjectDir(projectId);
        var existing = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (existing is null)
            return false;

        var charDir = _projects.GetCharactersDir(projectId);
        Directory.CreateDirectory(charDir);
        var bestVariant = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_01.png");

        // Demote lock → variant 1 (best option) instead of discarding the image
        try
        {
            File.Copy(existing, bestVariant, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not preserve locked image as variant 1: {ex.Message}", ex);
        }

        try
        {
            File.Delete(existing);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Demoted lock to variant_01 but could not delete {Path}", existing);
            // Still treat as unlock if variant was written and lock is gone, or if lock remains
            // try again once — if still present, report failure so UI doesn't lie
            if (File.Exists(existing))
                throw new InvalidOperationException(
                    $"Saved best-so-far as variant 1, but could not remove locked file: {ex.Message}", ex);
        }

        _log.LogInformation(
            "Unlocked {CharKey}: preserved {Ref} as {Variant}",
            charKey, Path.GetFileName(existing), Path.GetFileName(bestVariant));
        return true;
    }

    // ---- helpers ----

    /// <summary>Locked ref if present, else variant_01 (best-so-far after unlock).</summary>
    private string? ResolvePreferredImagePath(string projectId, string charKey, string charDir)
    {
        var locked = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (locked is not null) return locked;
        var best = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_01.png");
        if (File.Exists(best) && new FileInfo(best).Length >= 64)
            return best;
        return null;
    }

    /// <summary>
    /// Resolves the shared costume-only reference plate for a wardrobe lock group, generating
    /// it once (text-only, no character identity involved) if it doesn't exist yet. Every
    /// character whose seed points at <paramref name="wardrobeKey"/> reuses this same image as
    /// a wardrobe anchor — see the COSTUME REFERENCE clause built into
    /// <see cref="GrokImageClient.EditVariantsAsync"/> — so their coat/hat/badge design stays
    /// pixel-identical instead of being independently re-imagined per character/per variant.
    /// </summary>
    private async Task<string?> EnsureWardrobeReferenceAsync(
        string projectId,
        string wardrobeKey,
        JsonElement wardrobeSeed,
        string imageModel,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var existing = _projects.ResolveWardrobeRefPath(projectId, wardrobeKey);
        if (existing is not null)
            return existing;

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var charDir = _projects.GetCharactersDir(projectId);
        Directory.CreateDirectory(charDir);

        var description = wardrobeSeed.TryGetProperty(DescriptionKey, out var d) ? d.GetString() ?? "" : "";
        var descSafe = CharacterVisualTextScrubber.ScrubVisualProse(description).Trim().TrimEnd('.');
        var projectStyle = ReadProjectRenderStyleLock(projectDir);
        var styleClause = !string.IsNullOrWhiteSpace(projectStyle)
            ? projectStyle!.Trim()
            : "Photoreal live-action continuity reference.";

        var prompt =
            "COSTUME REFERENCE PLATE — this is NOT a character portrait; no individual identity matters here. " +
            styleClause + " " +
            "Show ONLY the uniform/wardrobe design, worn by a generic, unremarkable, forgettable figure " +
            "so the garment reads clearly. This figure's face/likeness will NEVER be reused as any " +
            "character's identity — only the coat, hat, badge, and garment details are meaningful. " +
            "Plain soft background, head-to-torso framing, facing camera, one clean continuity image. " +
            "No collage, no split views, no text overlays, no labels. " +
            "WARDROBE TO DEPICT: " +
            (string.IsNullOrWhiteSpace(descSafe) ? "period uniform as described in project notes." : descSafe + ".");

        onProgress?.Invoke($"Generating shared uniform reference for '{wardrobeKey}' (one-time)…");
        var blobs = await _images.GenerateVariantsAsync(
            prompt, 1, aspectRatio: "1:1", model: imageModel, ct: ct).ConfigureAwait(false);
        if (blobs.Count < 1)
            throw new InvalidOperationException($"No image generated for wardrobe reference '{wardrobeKey}'");

        var fileName = ProjectStore.WardrobeRefFileName(wardrobeKey);
        var full = Path.Combine(charDir, fileName);
        await File.WriteAllBytesAsync(full, blobs[0], ct).ConfigureAwait(false);
        onProgress?.Invoke($"Saved shared uniform reference → {fileName}");

        try
        {
            await _costs.RecordImageGenerationAsync(
                projectId, 1, imageModel, quality: true,
                character: wardrobeKey, userId: CurrentUserId, ct: ct);
        }
        catch (Exception costEx)
        {
            _log.LogWarning(costEx, "Could not record wardrobe reference image cost");
        }

        return full;
    }

    /// <summary>
    /// Project medium from cast extract (<c>render_style_lock</c>). Null when missing.
    /// </summary>
    internal static string? ReadProjectRenderStyleLock(string projectDir)
    {
        // Structured metadata from adaptation (or cast) — never regex over Fountain prose.
        var vision = ProjectVisionMeta.TryRead(projectDir);
        if (!string.IsNullOrWhiteSpace(vision?.RenderStyleLock))
            return vision!.RenderStyleLock!.Trim();

        try
        {
            var castPath = Path.Combine(projectDir, "source", ScreenplayService.CastSeedsFileName);
            if (File.Exists(castPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
                if (doc.RootElement.TryGetProperty("render_style_lock", out var rsl) &&
                    rsl.ValueKind == JsonValueKind.String &&
                    rsl.GetString() is { Length: > 0 } s)
                    return s.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>True when source/book_images contains real image files (reference plates only).</summary>
    internal static bool ProjectHasBookIllustrations(string projectDir)
    {
        var imgDir = Path.Combine(projectDir, "source", "book_images");
        if (!Directory.Exists(imgDir)) return false;
        try
        {
            return Directory.EnumerateFiles(imgDir, "*.*", SearchOption.AllDirectories).Any(f =>
            {
                var e = Path.GetExtension(f);
                return e.Equals(".png", StringComparison.OrdinalIgnoreCase)
                       || e.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                       || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                       || e.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                       || e.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch { return false; }
    }

    public static bool PrefersIllustratedPortraitStyle(
        string? projectRenderStyleLock,
        bool hasImageHints,
        bool isAnimal,
        bool hasBookSource = false)
    {
        var style = projectRenderStyleLock ?? "";
        if (style.Length > 0)
        {
            // Explicit live-action / photoreal project wins over picture-book default.
            if (RegexContains(style,
                    @"\b(photoreal|photo-?real|live[- ]?action|cinematic|film photography|" +
                    @"period drama|gothic drama|naturalistic skin|continuity portrait)\b"))
                return false;
            if (RegexContains(style,
                    @"\b(picture[- ]?book|illustration|illustrated|cartoon|painted cartoon|" +
                    @"children'?s book|storybook|stylized 3d|cg animated)\b"))
                return true;
        }

        // No style in screenplay/cast: do not guess from files. Photoreal until medium is written.
        return false;
    }


    private static bool RegexContains(string text, string pattern) =>
        CommonRegex.IsMatch(
            text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static (string Prompt, bool Illustrated) BuildDesignPrompt(
        string charKey,
        JsonElement seedInfo,
        bool hasImageHints,
        string? descriptionOverride = null,
        string? visualLockOverride = null,
        string? projectRenderStyleLock = null,
        string? wardrobeLockDescription = null,
        bool hasIdentityRefs = true,
        bool hasCostumeRef = false,
        bool hasBookIllustrations = false)
    {
        var description = !string.IsNullOrWhiteSpace(descriptionOverride)
            ? descriptionOverride!
            : seedInfo.TryGetProperty(DescriptionKey, out var d) ? d.GetString() ?? "" : "";
        var visualLock = !string.IsNullOrWhiteSpace(visualLockOverride)
            ? visualLockOverride!
            : seedInfo.TryGetProperty("visual_lock", out var vlck) ? vlck.GetString() ?? "" : "";
        var ageBand = seedInfo.TryGetProperty("age_band", out var ab) ? ab.GetString() ?? "" : "";
        var variantOf = seedInfo.TryGetProperty("variant_of", out var vo) ? vo.GetString() ?? "" : "";
        var display =
            seedInfo.TryGetProperty("canonical_given_name", out var cn) && cn.GetString() is { Length: > 0 } cname
                ? cname
                : seedInfo.TryGetProperty("voice_label", out var vl) && vl.GetString() is { Length: > 0 } lab
                    ? lab
                    : charKey.Replace("Character_", "").Replace("_", " ");

        var isAnimalDog = CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
            charKey, ageBand, description, visualLock, "dog");
        var isAnimalOther =
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "cat") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "rabbit") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "bunny") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "bear") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "fox");
        var isAnimal = isAnimalDog || isAnimalOther;
        var isHumanAdult = CharacterVisualTextScrubber.IsHumanAdultCharacter(
            charKey, ageBand, description, visualLock);

        // Illustrated vs photoreal from structured vision_meta / STYLE LOCK — not file type.
        var illustrated = PrefersIllustratedPortraitStyle(
            projectRenderStyleLock, hasImageHints, isAnimal, hasBookSource: false);

        var speciesClause = "";
        if (ageBand.StartsWith("child", StringComparison.OrdinalIgnoreCase) ||
            charKey.EndsWith("_Young", StringComparison.OrdinalIgnoreCase))
        {
            speciesClause =
                "SPECIES/AGE: CHILD human — child proportions, youthful face; not adult, not aged-up. ";
        }
        else if (ageBand.StartsWith("teen", StringComparison.OrdinalIgnoreCase) ||
                 charKey.EndsWith("_Teen", StringComparison.OrdinalIgnoreCase))
        {
            speciesClause =
                "SPECIES/AGE: TEEN human — younger than adult version; not middle-aged. ";
        }
        else if (isAnimalDog)
        {
            speciesClause = illustrated
                ? "SPECIES: DOG character (animal), not a human. " +
                  "Keep the illustrated breed/look from the book art — not a photoreal stock dog. " +
                  "Natural fur/coat only unless a reference image clearly shows clothing. "
                : "SPECIES: DOG (animal), not a human. Photoreal coat and anatomy matching the project medium. " +
                  "Natural fur only unless clothing is part of the locked look. ";
        }
        else if (isAnimalOther)
        {
            speciesClause = illustrated
                ? "SPECIES: animal character, not a person. Match the book-art creature; " +
                  "not photoreal wildlife photography. No costume unless clearly in refs. "
                : "SPECIES: animal character, not a person. Photoreal anatomy matching the project medium. ";
        }
        else if (isHumanAdult)
        {
            speciesClause = illustrated
                ? "SPECIES: HUMAN adult — a person, not an animal. " +
                  "All characters are rendered as in a children's picture book; not photoreal stock photography. "
                : "SPECIES: HUMAN adult — a real person, not an animal, not a drawing. " +
                  "Photoreal skin texture and period wardrobe matching the project medium. ";
        }


        var familyClause = "";
        if (!string.IsNullOrWhiteSpace(variantOf))
        {
            familyClause =
                $"FAMILY: younger version of {variantOf} " +
                "(same ethnicity/hair family, recognizable related features). ";
        }

        // Shared uniform group (see wardrobe_lock_tokens / CharacterDesignService.EnsureWardrobeReferenceAsync):
        // authoritative wardrobe text lives on the group, not repeated/re-invented per character.
        var wardrobeClause = "";
        if (!string.IsNullOrWhiteSpace(wardrobeLockDescription))
        {
            var wardrobeSafe = CharacterVisualTextScrubber.ScrubVisualProse(wardrobeLockDescription)
                .Trim().TrimEnd('.');
            wardrobeClause =
                $"SHARED UNIFORM (hard constraint — must match every other character in this same " +
                $"uniform group exactly, not just similar): {wardrobeSafe}. ";
        }

        // Prompt-time text prep: keep filmable words; image model still gets strong IGNORE rules.
        // Only known human seeds may get "human — not an animal" in cross-species medium rewrites.
        var descSafe = CharacterVisualTextScrubber.ScrubVisualProse(
            description, disambiguateCrossSpeciesAsHuman: isHumanAdult && !isAnimal);
        var visualSafe = CharacterVisualTextScrubber.ScrubVisualProse(
            visualLock, disambiguateCrossSpeciesAsHuman: isHumanAdult && !isAnimal);

        // Priority-ordered instructions work better than a long free-form paragraph for Imagine.
        const string ignoreRules =
            "IGNORE in the text notes (do not draw these): " +
            "later-story wardrobe or outfit changes; 'later wears…', 'afterwards…', 'once X is on…'; " +
            "scene actions and plot (pointing, offering treats, sleeping, chasing); " +
            "figurative nicknames or idioms taken as objects (food-as-hat, metaphor props); " +
            "model-sheet labels, arrows, color swatches, UI chrome. ";

        const string outputRules =
            "OUTPUT: one clean continuity portrait, head and upper body, facing camera, " +
            "plain soft background. No collage, no split views, no text overlays. ";

        // Honor project render_style_lock (live-action period, picture-book, etc.).
        // Default was always picture-book — wrong for photoreal projects; style lock decides.
        string styleLock;
        if (!string.IsNullOrWhiteSpace(projectRenderStyleLock))
        {
            var cleaned = projectRenderStyleLock.Trim().TrimEnd('.');
            if (!cleaned.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase))
                cleaned = "STYLE LOCK: " + cleaned;
            styleLock = illustrated
                ? $"{cleaned}. Match that illustrated medium exactly — not photoreal stock, not a different art style. "
                : $"{cleaned}. Photoreal / live-action continuity portrait — natural skin pores and fabric, " +
                  "NOT a sketch, NOT pencil drawing, NOT illustration, NOT cartoon, NOT anime, NOT 3D CGI beauty face. ";
        }
        else if (illustrated)
        {
            styleLock =
                "STYLE LOCK (hard): children's picture-book illustration matching the book references — " +
                "soft painted cartoon / illustrated medium, simplified shapes, gentle shading. " +
                "NOT photorealistic, NOT live-action photography, NOT stock-photo animal, " +
                "NOT hyper-detailed fur photography, NOT 3D CGI render. " +
                "If book plates are attached, copy their line, color, and medium exactly. ";
        }
        else
        {
            styleLock =
                "STYLE LOCK (hard): photoreal live-action continuity portrait — naturalistic face and wardrobe. " +
                "NOT a sketch, NOT pencil/charcoal drawing, NOT illustration, NOT cartoon, NOT anime. ";
        }

        var lookBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(descSafe))
            lookBits.Add(descSafe.Trim().TrimEnd('.'));
        if (!string.IsNullOrWhiteSpace(visualSafe))
            lookBits.Add("Hard constraints: " + visualSafe.Trim().TrimEnd('.'));
        var lookNotes = lookBits.Count > 0
            ? string.Join(". ", lookBits) + "."
            : "Match the character identity from context.";

        if (hasImageHints)
        {
            var matchBody = !hasIdentityRefs
                ? "Take ONLY the wardrobe/costume from the attached costume reference; " +
                  "face, hair, build, and species come from the text description below, never from that image. "
                : hasCostumeRef
                    // Identity ref AND costume ref both attached: split them explicitly.
                    // Without this, "match ... clothing from the preferred reference photo"
                    // competes with the costume reference and each character's own (possibly
                    // stale/pre-lock) identity photo wins on wardrobe details some of the time.
                    ? "Match face, hair, and build from the preferred reference photo/portrait — " +
                      "but NOT its wardrobe. Coat, hat/cap, and badge come ONLY from the separately " +
                      "labeled costume reference image below, never from the identity photo, even if " +
                      "the identity photo shows different or older wardrobe. "
                    : isAnimal
                        ? (illustrated
                            ? "Match species, coat pattern, ears, and face shape from the illustrated book references. "
                            : "Match species, coat, and face from the attached reference images. ")
                        : (illustrated
                            ? "Match face, hair, and default clothing from the preferred illustrated reference. "
                            : "Match face, hair, and default clothing from the preferred reference photo/portrait. ");

            var priority1 = hasIdentityRefs
                ? "PRIORITY 1 — IMAGES: The first attached image is the authoritative identity AND art style. " +
                  "Further images are the SAME character (markings/style only) or a costume reference " +
                  "as separately labeled below. " +
                  "When text and images disagree, trust the images for face/identity" +
                  (hasCostumeRef
                      ? " — and trust the separately labeled costume reference (not the identity photo) " +
                        "for wardrobe/hat/badge, even where they differ. "
                      : ". ")
                : "PRIORITY 1 — IMAGE: the attached reference is a COSTUME REFERENCE ONLY (see label below) — " +
                  "it shows the required wardrobe, not this character's face. This character's face/identity " +
                  "comes entirely from the text description below, not from that image. ";

            var withHints =
                $"CHARACTER CONTINUITY PORTRAIT of {display}. " +
                styleLock +
                priority1 +
                "Skip any reference that is mostly printed text with no character art. " +
                "Do not redesign; do not invent a new outfit not clearly visible in the character/costume art. " +
                matchBody +
                speciesClause +
                familyClause +
                wardrobeClause +
                "PRIORITY 2 — BASE LOOK ONLY: default everyday appearance for a faceplate/lock. " +
                (isAnimal
                    ? "If book art shows an animal without clothes, draw no clothes or costumes. "
                    : "Use only default clothes visible in refs; do not add later-story costumes. ") +
                ignoreRules +
                $"PRIORITY 3 — TEXT NOTES (secondary hints only): {lookNotes} " +
                outputRules;
            return (withHints, illustrated);
        }

        var noHints =
            $"CHARACTER CONTINUITY PORTRAIT of {display}. " +
            styleLock +
            "BASE LOOK ONLY — default everyday appearance for a faceplate/lock, not a story beat. " +
            speciesClause +
            familyClause +
            wardrobeClause +
            ignoreRules +
            (isAnimal
                ? (illustrated
                    ? "Illustrated animal appearance; clothing only if text clearly states it as the usual look (not 'later'). "
                    : "Photoreal animal appearance; clothing only if text states it as the usual look. ")
                : "Default clothes only; skip later-story outfit changes. ") +
            $"LOOK: {lookNotes} " +
            outputRules;
        return (noHints, illustrated);
    }

    private static List<string> ResolveBookRefPaths(string projectDir, JsonElement seedInfo, int maxRefs)
    {
        // Only seed-tracked plates; never text-only / sampled paths
        var rels = new List<string>();
        foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
        {
            if (!seedInfo.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (ProjectStore.IsTextOnlyPlatePath(s!)) continue;
                if (!rels.Contains(s!, StringComparer.OrdinalIgnoreCase))
                    rels.Add(s!);
            }
            if (rels.Count > 0)
                break;
        }

        var full = new List<string>();
        foreach (var rel in rels)
        {
            if (full.Count >= maxRefs) break;
            var norm = rel.Replace('\\', '/').TrimStart('/');
            if (norm.Contains("..", StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(Path.Combine(projectDir, norm.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(path))
                full.Add(path);
        }

        return full;
    }

    private async Task<string> GetConfigStringAsync(
        string projectId,
        string key,
        string fallback,
        CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        if (cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback;
        return fallback;
    }

    private static bool IsVoiceOnly(string key, JsonElement info)
    {
        // Prefer cast seed policy. Do not force voice-only just because key is "Narrator"
        // (on-camera confessor / POV roles are common). Shared mechanism:
        // CastKindClassifier.IsVoiceOnlyPolicy.
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("display_name_policy", out var pol))
            return CastKindClassifier.IsVoiceOnlyPolicy(pol.GetString());
        return false;
    }

}

public sealed class CharacterDesignResult
{
    public string CharKey { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public List<string> BookRefs { get; set; } = new();
    public string? EditError { get; set; }
    /// <summary>True when an iterative plate tweak was locked as preferred (no multi-variant pick).</summary>
    public bool LockedAsPreferred { get; set; }
    public int? PreviousVariantIndex { get; set; }
    public int? NewVariantIndex { get; set; }
}

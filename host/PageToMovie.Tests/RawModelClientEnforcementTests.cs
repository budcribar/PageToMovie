using System.Text.RegularExpressions;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

/// <summary>
/// Phase 3 of the AI-call feedback loop plan: "no raw client call outside the wrapper." Scans
/// <c>PageToMovie.Engine</c> for direct calls to <c>IChatClient.CompleteAsync</c> /
/// <c>IVisionClient.CompleteWithImagesAsync</c> / <c>ClassifyCharactersOnImageAsync</c> /
/// <c>TranscribePageAsync</c> and asserts every call site is accounted for — either it's inside a
/// sanctioned wrapper (a <c>ModelBacked/*</c> operation, or a coverage-retry classifier routed
/// through <c>AiRetryPolicy.RunWithCoverageRetryAsync</c>/<c>BeatChatClassifierBase</c>), or it's an
/// explicitly documented bespoke call site in <see cref="KnownBespokeDebt"/> with a reason.
///
/// This does NOT block on paying off the debt first (Phase 1 is still partial) — it only fails on
/// NEW, undocumented drift. The allowlist below is the living todo list for finishing Phase 1;
/// as a gate gets migrated onto ValidatedModelOperation, remove its file from
/// <see cref="KnownBespokeDebt"/> so the test starts enforcing it too.
/// </summary>
public sealed class RawModelClientEnforcementTests
{
    /// <summary>
    /// Files that ARE the sanctioned wrapper — an <c>IModelOperation</c> implementation (calls the
    /// raw client because that's literally its job) or a coverage-retry classifier's own
    /// <c>callChat</c> lambda passed to <c>AiRetryPolicy.RunWithCoverageRetryAsync</c>. A raw call
    /// here is the wrapper working as designed, not a violation.
    /// </summary>
    private static readonly HashSet<string> SanctionedWrapperFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // IChatClient/IVisionClient implementations that route/decorate rather than call a concrete
        // provider directly — same category as GrokChatClient.cs etc. (which DEFINE the methods, so
        // never match the "." call-site regex and need no explicit exclusion). These DO call the
        // interface method via a "." (Resolve(model).CompleteAsync(...) / _inner.CompleteAsync(...)),
        // so the regex catches them; they're not a bespoke caller bypassing the wrapper, they're the
        // client layer itself. Found by this test failing on its first run, 2026-08-08.
        "CachingChatClient.cs",
        "MultiProviderChatClient.cs",
        "MultiProviderVisionClient.cs",
        // ModelBacked/* — the IModelOperation implementations themselves.
        "AiActionOverheadClassifier.cs",
        "CastModelOperations.cs",
        "Stage1FountainOperation.cs",
        "Stage2DirectiveOperation.cs",
        "MultimodalReviewOperation.cs",
        "PortraitStyleGateOperation.cs",
        // Coverage-retry classifiers — already routed through ValidatedCoverageOperation via
        // AiRetryPolicy.RunWithCoverageRetryAsync (see pagetomovie_ai_call_feedback_loop memory:
        // "beat classifiers are already migrated" finding, 2026-08-07).
        "AmbientSfxClassifier.cs",
        "BeatChatClassifierBase.cs",
        "ExtendCutClassifier.cs",
        "OnScreenCastClassifier.cs",
        "ShotPlanRefiningClassifier.cs",
        "SilentBeatActionClassifier.cs",
        "SpeciesKindClassifier.cs",
        "WardrobeContinuityClassifier.cs",
    };

    /// <summary>
    /// Known bespoke call sites, not yet migrated onto <c>ValidatedModelOperation</c> — each entry is
    /// a real gap (missing corrective retry, no schema validation, no provenance trace), documented
    /// rather than silently allowed. Audited 2026-08-07 alongside the portrait-style-gate migration
    /// (the pilot for Phase 1 — see <c>ModelBacked/PortraitStyleGateOperation.cs</c>).
    /// </summary>
    private static readonly HashSet<string> KnownBespokeDebt = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClipDialogueVerificationService.cs",   // dialogue-verify gate — most complex response shape, own follow-up pass
        "CharacterBookPlateService.cs",         // cast-on-image gate — already tolerates per-image failure in its own loop
        "SceneMusicCompositionService.cs",      // music-supervisor vision call (per-scene score prompts)
        "SceneMusicScoringService.cs",          // music scoring chat call
        "LearningProposalService.cs",           // QC-fail → house-rule learning proposals
        "ProjectVisionMeta.cs",                 // vision metadata chat call
        "PlateRankClassifier.cs",               // single-shot, no coverage/correction concept — smaller fix than the others
        "BookPrepareService.cs",                // book-page OCR transcription
        "JitBenchmarkService.cs",               // JIT timing calibration benchmark, not the live per-generation pipeline
        "LookVariantPicker.cs",                 // look variant picker vision call
    };

    private static readonly Regex RawCallPattern = new(@"\.(CompleteAsync|CompleteWithImagesAsync|ClassifyCharactersOnImageAsync|TranscribePageAsync)\(", RegexOptions.Compiled, CommonRegex.Timeout);

    [Fact]
    public void Every_raw_model_client_call_site_is_wrapped_or_documented_debt()
    {
        var root = FindEngineSourceRoot();
        var undocumented = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var fileName = Path.GetFileName(file);
            // The actual client implementations DEFINE these methods (public async Task<string>
            // CompleteAsync(...)) — that's a declaration, not a "." member-access call, so the regex
            // below naturally doesn't match them. No exclusion needed for GrokChatClient.cs etc.
            if (SanctionedWrapperFiles.Contains(fileName) || KnownBespokeDebt.Contains(fileName))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith('*'))
                    continue;
                if (RawCallPattern.IsMatch(lines[i]))
                    undocumented.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(undocumented.Count == 0,
            "New raw model-client call site(s) found outside the wrapper and outside the documented " +
            "bespoke-debt allowlist. Either route the call through ValidatedModelOperation (see " +
            "ModelBacked/PortraitStyleGateOperation.cs for a worked example), or add the file to " +
            "RawModelClientEnforcementTests.KnownBespokeDebt with a reason:\n" +
            string.Join("\n", undocumented.Take(20)));
    }

    /// <summary>Every documented debt file must actually still exist and still contain a raw call —
    /// catches stale entries once a gate gets migrated, so the allowlist doesn't quietly rot.</summary>
    [Fact]
    public void KnownBespokeDebt_entries_are_still_accurate()
    {
        var root = FindEngineSourceRoot();
        var stale = new List<string>();

        foreach (var fileName in KnownBespokeDebt)
        {
            var matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToList();
            if (matches.Count == 0)
            {
                stale.Add($"{fileName}: file no longer exists — remove from KnownBespokeDebt");
                continue;
            }
            var stillHasRawCall = matches.Any(f => File.ReadAllLines(f).Any(l => RawCallPattern.IsMatch(l)));
            if (!stillHasRawCall)
                stale.Add($"{fileName}: no longer calls a raw client method — migration landed, remove from KnownBespokeDebt");
        }

        Assert.True(stale.Count == 0, "Stale KnownBespokeDebt entries:\n" + string.Join("\n", stale));
    }

    private static string FindEngineSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "host", "PageToMovie.Engine");
            if (Directory.Exists(candidate))
                return candidate;
            // When tests run from host/PageToMovie.Tests/bin/...
            candidate = Path.Combine(dir.FullName, "PageToMovie.Engine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("PageToMovie.Engine source directory not found from test base directory.");
    }
}

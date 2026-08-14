using System.Text.RegularExpressions;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

/// <summary>
/// Budget, quality gate, and SingleShotFirst → ChunkFallback path selection.
/// </summary>
public class BookToFountainPathTests
{
    [Fact]
    public void ResolvePromptBudget_grok_allows_large_single_shot()
    {
        var b = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        Assert.Equal("grok-4.5", b.ModelId);
        Assert.True(
            b.SingleShotBookMaxChars > AdaptationFountain.SingleShotMaxChars,
            $"expected single-shot max >> legacy 28k, got {b.SingleShotBookMaxChars}");
        Assert.InRange(b.ChunkSoftMaxChars, 4_000, b.SingleShotBookMaxChars);
        Assert.Equal(AdaptationFountain.MaxAdaptChunks, b.MaxChunks);
    }

    [Fact]
    public void ResolvePromptBudget_known_model_trusts_catalog_window_past_the_conservative_default()
    {
        // grok-4.5's real catalog window (500k tokens) is large enough that its token-derived
        // budget hits the absolute safety ceiling, not the conservative "we don't really know"
        // default — that default exists for models we have no verified data on, not for ones we do.
        var b = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        Assert.True(
            b.SingleShotBookMaxChars > AdaptationFountain.DefaultSingleShotBookMaxChars,
            $"expected a known large-context model to exceed the conservative default, got {b.SingleShotBookMaxChars}");
        Assert.Equal(AdaptationFountain.AbsoluteSingleShotCeiling, b.SingleShotBookMaxChars);
    }

    [Fact]
    public void ResolvePromptBudget_unknown_model_still_usable()
    {
        var b = AdaptationFountain.ResolvePromptBudget("some-future-chat");
        Assert.True(b.SingleShotBookMaxChars >= AdaptationFountain.SingleShotMaxChars);
        Assert.True(b.ChunkSoftMaxChars >= 4_000);
    }

    [Fact]
    public void ResolvePromptBudget_unknown_model_stays_at_conservative_default()
    {
        // No catalog entry → no verified context window → stay under the conservative default
        // rather than the (unfounded) 128k-token guess pushing past it.
        var b = AdaptationFountain.ResolvePromptBudget("some-future-chat");
        Assert.Equal(AdaptationFountain.DefaultSingleShotBookMaxChars, b.SingleShotBookMaxChars);
    }

    [Fact]
    public void FitsSingleShot_respects_budget()
    {
        var budget = new AdaptationFountain.PromptBudget
        {
            ModelId = "test",
            SingleShotBookMaxChars = 10_000,
            ChunkSoftMaxChars = 5_000,
            MaxChunks = 4,
            ReservedOverheadChars = 1_000,
        };
        Assert.True(AdaptationFountain.FitsSingleShot(new string('a', 9_000), budget));
        Assert.False(AdaptationFountain.FitsSingleShot(new string('a', 10_001), budget));
    }

    [Fact]
    public void ShouldChunkFallback_false_for_tiny_book()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        var tiny = "--- PAGE 1 ---\nA short picture-book line about a dog in the sun.\n";
        Assert.False(AdaptationFountain.ShouldChunkFallback(tiny, budget));
    }

    [Fact]
    public void ShouldChunkFallback_true_for_long_chaptered_book()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        var book = BuildChapteredBook(chapters: 12, bodyChars: 3_000);
        Assert.True(book.Length >= AdaptationFountain.MinBookCharsForChunkFallback);
        Assert.True(AdaptationFountain.ShouldChunkFallback(book, budget));
    }

    [Fact]
    public void ResolveMaxChunks_stays_at_default_for_typical_book()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        var book = BuildChapteredBook(chapters: 12, bodyChars: 3_000); // ~40K chars
        var resolved = AdaptationFountain.ResolveMaxChunks(book, budget);
        Assert.Equal(budget.MaxChunks, resolved);
    }

    [Fact]
    public void ResolveMaxChunks_scales_up_for_a_dracula_scale_book()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        // ~840K chars, same order of magnitude as Dracula's stripped content —
        // needs ~21 chunks at a 40K soft-max, well past the flat 8-chunk default.
        var book = BuildChapteredBook(chapters: 200, bodyChars: 4_100);
        Assert.True(book.Length > 800_000, $"test book too small: {book.Length}");

        var resolved = AdaptationFountain.ResolveMaxChunks(book, budget);

        Assert.True(resolved > budget.MaxChunks,
            $"expected scaling past default {budget.MaxChunks}, got {resolved}");
        Assert.True(resolved <= AdaptationFountain.AbsoluteMaxAdaptChunks);
    }

    [Fact]
    public void ResolveMaxChunks_never_exceeds_absolute_ceiling_for_extreme_books()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        var huge = new string('a', 5_000_000);
        var resolved = AdaptationFountain.ResolveMaxChunks(huge, budget);
        Assert.Equal(AdaptationFountain.AbsoluteMaxAdaptChunks, resolved);
    }

    [Fact]
    public void ChunkBookForAdaptation_with_resolved_chunks_avoids_one_oversized_final_chunk()
    {
        var budget = AdaptationFountain.ResolvePromptBudget("grok-4.5");
        var book = BuildChapteredBook(chapters: 200, bodyChars: 4_100); // ~840K chars

        // Old behavior: flat MaxAdaptChunks (8) forces most of the book into the last chunk.
        var flatChunks = AdaptationFountain.ChunkBookForAdaptation(
            book, AdaptationFountain.MaxAdaptChunks, budget.ChunkSoftMaxChars);
        Assert.True(flatChunks[^1].Length > budget.ChunkSoftMaxChars * 4,
            $"expected the flat-cap regression to reproduce here, last chunk was {flatChunks[^1].Length}");

        // Fixed behavior: a resolved (scaled) chunk count keeps every chunk close to soft-max.
        var resolvedMax = AdaptationFountain.ResolveMaxChunks(book, budget);
        var scaledChunks = AdaptationFountain.ChunkBookForAdaptation(
            book, resolvedMax, budget.ChunkSoftMaxChars);
        foreach (var chunk in scaledChunks)
        {
            Assert.True(chunk.Length <= budget.ChunkSoftMaxChars * 2,
                $"chunk of {chunk.Length} chars is more than 2x the {budget.ChunkSoftMaxChars} soft max");
        }
    }

    [Fact]
    public void StripFountainPageBreaks_removes_standalone_marker_after_title_page()
    {
        var fountain = "Title: Test\nAuthor: Unit\n\n===\n\nFADE IN:\n\nINT. ROOM - DAY\n\nSomething happens.\n";
        var cleaned = AdaptationFountain.StripFountainPageBreaks(fountain);
        Assert.DoesNotContain("===", cleaned);
        Assert.Contains("FADE IN:", cleaned);
        Assert.Contains("INT. ROOM - DAY", cleaned);
    }

    [Fact]
    public void StripFountainPageBreaks_leaves_normal_content_alone()
    {
        var fountain = "Title: Test\nAuthor: Unit\n\nFADE IN:\n\nINT. ROOM - DAY\n\nA sign reads: OPEN.\n";
        var cleaned = AdaptationFountain.StripFountainPageBreaks(fountain);
        Assert.Equal(fountain.TrimEnd() + "\n", cleaned);
    }

    [Fact]
    public void EnsureFadeIn_inserts_before_first_scene_heading_when_missing()
    {
        var fountain = "Title: Test\nAuthor: Unit\n\nINT. ROOM - DAY\n\nSomething happens.\n";
        var fixed_ = AdaptationFountain.EnsureFadeIn(fountain);
        Assert.Contains("FADE IN:\n\nINT. ROOM - DAY", fixed_, StringComparison.Ordinal);
        Assert.Single(CommonRegex.Matches(fixed_, "FADE IN:", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void EnsureFadeIn_is_a_noop_when_already_present()
    {
        var fountain = "Title: Test\nAuthor: Unit\n\nFADE IN:\n\nINT. ROOM - DAY\n\nSomething happens.\n";
        var unchanged = AdaptationFountain.EnsureFadeIn(fountain);
        Assert.Equal(fountain, unchanged);
    }

    [Fact]
    public void StripFountainPageBreaks_then_EnsureFadeIn_recovers_from_a_straight_substitution()
    {
        // The exact regression this pair is for: the model emitted === where FADE IN: should
        // have been (not alongside it) — stripping the === alone would leave no FADE IN: at all.
        var fountain = "Title: Test\nAuthor: Unit\n\n===\n\nINT. ROOM - DAY\n\nSomething happens.\n";
        var stripped = AdaptationFountain.StripFountainPageBreaks(fountain);
        Assert.DoesNotContain("===", stripped);
        Assert.DoesNotContain("FADE IN", stripped, StringComparison.OrdinalIgnoreCase); // gap before the fix

        var recovered = AdaptationFountain.EnsureFadeIn(stripped);
        Assert.Contains("FADE IN:\n\nINT. ROOM - DAY", recovered, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateQuality_short_good_fountain_passes_single()
    {
        var book = "--- PAGE 1 ---\nA little dog naps by the warm fire tonight under soft blankets.\n";
        var fountain = GoodFountain(scenes: 3, withEnding: true);
        var gate = AdaptationFountain.EvaluateQuality(
            fountain, book, totalRuntimeMinutes: 8, AdaptationFountain.AdaptPath.Single);
        Assert.True(gate.Ok, gate.Reason);
        Assert.Equal("ok", gate.Reason);
    }

    [Fact]
    public void EvaluateQuality_structure_fail_is_hard()
    {
        var book = new string('x', 30_000);
        var gate = AdaptationFountain.EvaluateQuality(
            "not fountain at all", book, 20, AdaptationFountain.AdaptPath.Single);
        Assert.False(gate.Ok);
        Assert.True(gate.HasHardFailure);
        Assert.Contains("structure", gate.Failures);
    }

    [Fact]
    public void EvaluateQuality_long_book_short_draft_fails_single_soft()
    {
        var book = BuildChapteredBook(chapters: 20, bodyChars: 4_000);
        Assert.True(book.Length > 60_000);
        // Structurally valid but too thin for a long novel single-shot
        var thin = """
            Title: Thin
            Author: T

            INT. ROOM - DAY

            NARRATOR
            Once upon a time there was a very short summary of a long book that should not pass coverage.

            FADE OUT.

            THE END
            """;
        var gate = AdaptationFountain.EvaluateQuality(
            thin, book, totalRuntimeMinutes: 40, AdaptationFountain.AdaptPath.Single);
        Assert.False(gate.Ok, "expected soft coverage fail");
        Assert.False(gate.HasHardFailure);
        Assert.Contains(gate.Failures, f =>
            f.StartsWith("scene_count")
            || f == "suspiciously_short"
            || f.StartsWith("runtime_short"));
    }

    [Fact]
    public void EvaluateQuality_runtime_short_when_draft_far_below_natural()
    {
        // Real multi-word prose so density natural estimate is feature-scale (≥45 min).
        var book = BuildProseBook(chapters: 40, sentencesPerChapter: 80);
        Assert.True(NaturalRuntime.EstimateNaturalMinutes(book) >= 45,
            $"natural={NaturalRuntime.EstimateNaturalMinutes(book)} words={TextMetrics.CountWords(book)}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Thin Epic");
        sb.AppendLine("Author: H");
        sb.AppendLine();
        for (var i = 1; i <= 12; i++)
        {
            sb.AppendLine($"INT. PLACE {i} - DAY");
            sb.AppendLine();
            sb.AppendLine("NARRATOR");
            sb.AppendLine("A brief summary of what should have been a full episode.");
            sb.AppendLine();
        }
        sb.AppendLine("FADE OUT.");
        sb.AppendLine();
        sb.AppendLine("THE END");

        var gate = AdaptationFountain.EvaluateQuality(
            sb.ToString(), book, totalRuntimeMinutes: null, AdaptationFountain.AdaptPath.Single);
        Assert.False(gate.Ok, gate.Reason);
        Assert.Contains(gate.Failures, f => f.StartsWith("runtime_short"));
    }

    [Fact]
    public void EstimateDraftRuntimeMinutes_scales_with_body_words()
    {
        var shortDraft = """
            Title: S
            Author: A

            INT. ROOM - DAY

            HERO
            Hello.

            FADE OUT.

            THE END
            """;
        var longBody = string.Join(' ', Enumerable.Repeat("action dialogue visual beat", 800));
        var longDraft = $"""
            Title: L
            Author: A

            INT. ROOM - DAY

            {longBody}

            FADE OUT.

            THE END
            """;
        var shortMin = AdaptationFountain.EstimateDraftRuntimeMinutes(shortDraft);
        var longMin = AdaptationFountain.EstimateDraftRuntimeMinutes(longDraft);
        Assert.True(longMin > shortMin + 3, $"expected long {longMin} >> short {shortMin}");
    }

    [Fact]
    public void EstimateDraftRuntimeMinutes_ignores_sidecar_when_it_disagrees_2x()
    {
        var longBody = string.Join(' ', Enumerable.Repeat("action dialogue visual beat", 8000));
        var report = """{"source_complete":"yes","metrics":{"scenes":175,"speaking_cast":20,"body_words":12,"est_runtime_min":17.3},"issues":[],"spec_feedback":[]}""";
        var draft =
            "Title: Epic\nAuthor: H\n\nINT. HALL - DAY\n\n"
            + longBody
            + "\n\nFADE OUT.\n\nTHE END\n\n---ADAPTATION_REPORT---\n"
            + report
            + "\n---END_ADAPTATION_REPORT---\n";
        var minutes = AdaptationFountain.EstimateDraftRuntimeMinutes(draft);
        Assert.True(minutes > 40, $"expected word-count minutes, got {minutes}");
        Assert.True(AdaptationFountain.IsRuntimeSidecarSuspect(17.3, minutes));
    }

    [Fact]
    public void EstimateDraftRuntimeMinutes_keeps_sidecar_when_close()
    {
        var draft = """
            Title: Short
            Author: A

            INT. ROOM - DAY

            HERO
            Hello there friend.

            FADE OUT.

            THE END

            ---ADAPTATION_REPORT---
            {"source_complete":"yes","metrics":{"scenes":1,"speaking_cast":1,"body_words":3,"est_runtime_min":0.1},"issues":[],"spec_feedback":[]}
            ---END_ADAPTATION_REPORT---
            """;
        var minutes = AdaptationFountain.EstimateDraftRuntimeMinutes(draft);
        Assert.InRange(minutes, 0.05, 0.3);
    }

    [Fact]
    public void EvaluateQuality_multi_accepts_soft_scene_shortfall_if_structure_ok()
    {
        var book = BuildChapteredBook(chapters: 20, bodyChars: 4_000);
        var thin = """
            Title: Thin
            Author: T

            INT. ROOM - DAY

            NARRATOR
            Once upon a time there was a stitched partial that is still valid fountain structure for multi path.

            HERO
            Hello there friend.

            FADE OUT.

            THE END
            """;
        var gate = AdaptationFountain.EvaluateQuality(
            thin, book, totalRuntimeMinutes: 40, AdaptationFountain.AdaptPath.Multi);
        Assert.True(gate.Ok, gate.Reason);
        Assert.False(gate.HasHardFailure);
    }

    [Fact]
    public async Task Convert_short_book_uses_single_shot_only()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 4, withEnding: true));
        var book = "--- PAGE 1 ---\nA little dog naps by the warm fire tonight under soft blankets and dreams.\n"
                   + "--- PAGE 2 ---\nMorning comes and the dog stretches in the golden light of the kitchen.\n";
        var root = Path.GetTempPath();

        var (text, _) = await AdaptConvertAsync(
            title: "Short",
            bookText: book,
            chat: chat,
            model: "grok-4.5",
            totalRuntimeMinutes: 6,
            author: "A");

        Assert.True(AdaptationFountain.LooksLikeGoodFountain(text));
        // One adaptation call plus up to two focused VISION_META lifecycle attempts.
        Assert.InRange(chat.Calls, 1, 3);
        Assert.DoesNotContain(chat.UserPrompts, u => u.Contains("multi-chunk", StringComparison.OrdinalIgnoreCase)
            || u.Contains("BOOK_CHUNK 2/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Convert_medium_book_single_first_when_quality_ok()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 12, withEnding: true, padBody: 400));
        // ~40k–50k: under default 120k single-shot budget, over legacy 28k
        var book = BuildChapteredBook(chapters: 14, bodyChars: 3_200);
        Assert.InRange(book.Length, AdaptationFountain.SingleShotMaxChars + 1, AdaptationFountain.DefaultSingleShotBookMaxChars);

        var (text, _) = await AdaptConvertAsync(
            title: "Medium",
            bookText: book,
            chat: chat,
            model: "grok-4.5",
            totalRuntimeMinutes: 20);

        Assert.True(AdaptationFountain.LooksLikeGoodFountain(text));
        // Single-shot success plus up to two focused VISION_META lifecycle attempts.
        Assert.True(chat.Calls <= 3, $"expected single-shot plus metadata repair (≤3 calls), got {chat.Calls}");
        Assert.Contains(chat.UserPrompts, u => u.Contains("BOOK_CHUNK 1/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Convert_quality_soft_fail_triggers_chunk_fallback()
    {
        var chat = new RecordingChatClient(n =>
        {
            // First single-shot attempts (pass + coverage retry): thin draft (structure ok, coverage fail)
            if (n <= 2)
            {
                return """
                    Title: Thin
                    Author: T

                    INT. ROOM - DAY

                    NARRATOR
                    A brief opening only — not enough arc for the full novel length below.

                    HERO
                    We start.
                    """;
            }

            // Multi-chunk parts + merge: return per-chunk scenes
            return GoodFountain(scenes: 4, withEnding: true, padBody: 120);
        });

        var book = BuildChapteredBook(chapters: 18, bodyChars: 4_000);
        Assert.True(book.Length > 60_000);
        Assert.True(AdaptationFountain.ShouldChunkFallback(
            book, AdaptationFountain.ResolvePromptBudget("grok-4.5")));

        var progress = new List<string>();
        var (text, _) = await AdaptConvertAsync(
            title: "Long",
            bookText: book,
            chat: chat,
            model: "grok-4.5",
            totalRuntimeMinutes: 45,
            onProgress: s => progress.Add(s));

        Assert.True(AdaptationFountain.LooksLikeGoodFountain(text));
        Assert.True(chat.Calls >= 3, $"expected chunk fallback (≥3 calls), got {chat.Calls}");
        Assert.Contains(progress, p => p.Contains("multi-chunk", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Convert_over_budget_skips_single_goes_multi()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 3, withEnding: true, padBody: 80));
        var book = BuildChapteredBook(chapters: 10, bodyChars: 2_500);
        var tinyBudget = new AdaptationFountain.PromptBudget
        {
            ModelId = "tiny-test",
            SingleShotBookMaxChars = 5_000,
            ChunkSoftMaxChars = 6_000,
            MaxChunks = 4,
            ReservedOverheadChars = 1_000,
        };
        Assert.False(AdaptationFountain.FitsSingleShot(book, tinyBudget));

        var progress = new List<string>();
        var (text, _) = await AdaptConvertAsync(
            title: "Over",
            bookText: book,
            chat: chat,
            model: "grok-4.5",
            totalRuntimeMinutes: 15,
            onProgress: s => progress.Add(s),
            budgetOverride: tinyBudget);

        Assert.True(AdaptationFountain.LooksLikeGoodFountain(text));
        Assert.Contains(progress, p => p.Contains("exceeds model budget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(progress, p => p.Contains("single pass", StringComparison.OrdinalIgnoreCase));
        // Multi path should mention chunks
        Assert.True(chat.Calls >= 2, $"expected multi-chunk calls, got {chat.Calls}");
    }

    [Fact]
    public async Task ConvertWithMetadata_returns_clean_fountain_and_metadata()
    {
        var response = GoodFountain(scenes: 4, withEnding: true) + """

            ---VISION_META---
            {"visual_medium":"illustrated_picture_book","render_style_lock":"STYLE LOCK: painted storybook continuity","notes":"picture book"}
            ---END_VISION_META---
            """;
        var chat = new RecordingChatClient(_ => response);

        var (_, result) = await AdaptConvertAsync(
            title: "Metadata",
            bookText: "A small rabbit explores a painted nursery and returns home.",
            chat: chat,
            model: "grok-4.5");

        Assert.DoesNotContain("VISION_META", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.VisionMeta);
        Assert.Equal(ProjectVisionMeta.MediumIllustrated, result.VisionMeta!.VisualMedium);
        Assert.Equal(ProjectVisionMetaStatus.PrimaryResponse, result.VisionMetaStatus);
        Assert.Null(result.VisionMetaError);
    }

    [Fact]
    public async Task ConvertWithMetadata_reports_missing_trailer()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 4, withEnding: true));

        var (_, result) = await AdaptConvertAsync(
            title: "No metadata",
            bookText: "A traveler enters a room and tells a short story before leaving.",
            chat: chat,
            model: "grok-4.5");

        Assert.Null(result.VisionMeta);
        Assert.Equal(ProjectVisionMetaStatus.Missing, result.VisionMetaStatus);
        Assert.Contains("missing", result.VisionMetaError, StringComparison.OrdinalIgnoreCase);
    }

    
    private static async Task<(string Fountain, PageToMovie.Engine.ProjectAdaptationConversionResult Mapped)> AdaptConvertAsync(
        string title,
        string bookText,
        RecordingChatClient chat,
        string model = "grok-4.5",
        int totalRuntimeMinutes = 10,
        Action<string>? onProgress = null,
        AdaptationFountain.PromptBudget? budgetOverride = null,
        string? author = null)
    {
        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = bookText,
                Title = title,
                Author = author,
                TargetRuntimeMinutes = totalRuntimeMinutes,
                ModelId = model,
            },
            ChatCall.FromProgress(chat, model, onProgress is null ? null : new Progress<string>(onProgress)),
            budgetOverride: budgetOverride);
        var mapped = new PageToMovie.Engine.ProjectAdaptationConversionResult
        {
            Fountain = result.Fountain,
            VisionMeta = PageToMovie.Engine.ProjectVisionMeta.MapVision(result.VisionMeta),
            VisionMetaStatus = PageToMovie.Engine.ProjectVisionMeta.MapStatus(result.VisionMetaStatus),
            VisionMetaError = result.VisionMetaError,
        };
        return (result.Fountain, mapped);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string BuildChapteredBook(int chapters, int bodyChars)
    {
        var sb = new System.Text.StringBuilder();
        for (var c = 1; c <= chapters; c++)
        {
            sb.Append("CHAPTER ").Append(c).Append('\n');
            sb.Append(new string((char)('a' + (c % 26)), bodyChars));
            sb.Append(" chapter body ").Append(c).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Multi-word prose book for natural-runtime density (not a run of aaaa…).</summary>
    private static string BuildProseBook(int chapters, int sentencesPerChapter)
    {
        var sb = new System.Text.StringBuilder();
        for (var c = 1; c <= chapters; c++)
        {
            sb.Append("CHAPTER ").Append(c).Append('\n');
            for (var s = 0; s < sentencesPerChapter; s++)
            {
                sb.Append("The traveler crossed the stone bridge and spoke with the merchant about ships and storms. ");
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string GoodFountain(int scenes, bool withEnding, int padBody = 60)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine("NARRATOR");
            sb.AppendLine(new string('w', Math.Max(40, padBody)) + $" scene {i} action and description.");
            sb.AppendLine();
            sb.AppendLine("HERO");
            sb.AppendLine($"Line number {i} with enough dialogue text for the gate.");
            sb.AppendLine();
        }

        if (withEnding)
        {
            sb.AppendLine("FADE OUT.");
            sb.AppendLine();
            sb.AppendLine("THE END");
        }

        return sb.ToString();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<int, string> _responseForCall;

        public RecordingChatClient(Func<int, string> responseForCall) =>
            _responseForCall = responseForCall;

        public int Calls { get; private set; }
        public List<string> UserPrompts { get; } = new();
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            Calls++;
            UserPrompts.Add(userPrompt ?? "");
            return Task.FromResult(_responseForCall(Calls));
        }
    }
}

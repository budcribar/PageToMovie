using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Fountain;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ScreenplayServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public ScreenplayServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-screenplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        var opts = Options.Create(new PageToMovieOptions
        {
            WorkspaceRoot = _root,
        });
        _store = new ProjectStore(opts);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Save_draft_then_sign_off_approves_fountain_without_scenes_json()
    {
        const string projectId = "Demo";
        var fountain = """
            Title: Test Script
            Author: Unit Test

            INT. LAB - DAY

            A scientist adjusts a dial.

            SCIENTIST
            Almost there.

            EXT. ROOF - NIGHT

            Rain.
            """;

        var save = ScreenplayService.SaveDraft(_store, projectId, fountain);
        Assert.True(save.Ok);
        Assert.True(save.Status.DraftExists);
        Assert.False(save.Status.Dirty); // never approved yet — not "edited since approval"
        Assert.False(save.Status.Signed);
        Assert.True(save.Status.SceneHeadingCount >= 2);

        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);
        Assert.Equal(2, sign.SceneCount);
        Assert.True(sign.Status.Signed);
        Assert.False(sign.Status.Dirty);
        Assert.True(sign.Status.ReadyForShots);
        Assert.True(sign.HashChanged);

        // No intermediate scenes.json — Stage 2 reads Fountain
        var scenesPath = _store.ResolveScenesJsonPath(projectId);
        Assert.False(File.Exists(scenesPath));
        Assert.True(File.Exists(ScreenplayService.GetDraftPath(_store, projectId)));

        var model = ScreenplayService.TryBuildModelFromProject(_store, projectId);
        Assert.NotNull(model);
        Assert.Equal(2, (model!["scenes"] as System.Collections.ICollection)?.Count ?? 0);

        var sign2 = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign2.Ok);
        Assert.False(sign2.HashChanged);
    }

    [Fact]
    public void Edit_after_sign_off_marks_dirty_and_blocks_ready_until_reapprove()
    {
        const string projectId = "Demo";
        ScreenplayService.SaveDraft(_store, projectId, "INT. ROOM - DAY\n\nHello.\n");
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok);
        Assert.True(sign.Status.ReadyForShots);

        ScreenplayService.SaveDraft(_store, projectId, "INT. ROOM - DAY\n\nHello world.\n");
        var status = ScreenplayService.Get(_store, projectId).Status;
        Assert.True(status.Dirty);
        Assert.False(status.Signed);
        Assert.False(status.ReadyForShots);
    }

    [Fact]
    public void Import_as_draft_does_not_write_stage1()
    {
        const string projectId = "Demo";
        var r = ScreenplayService.ImportAsDraft(_store, projectId, "INT. A - DAY\n\nAction.\n", "mine.fountain");
        Assert.True(r.Ok);
        Assert.True(r.Status.DraftExists);
        var scenesPath = _store.ResolveScenesJsonPath(projectId);
        Assert.False(File.Exists(scenesPath));
    }

    [Fact]
    public void Offline_stub_draft_from_book_has_no_page_tags()
    {
        // CreateDraftFromBook (sync) is the offline stub — production uses chat.
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "book_full.txt"),
            "--- PAGE 1 ---\nOnce upon a time there was a dog who loved naps.\n\n" +
            "--- PAGE 2 ---\nHe slept by the fire.\n");

        var r = ScreenplayService.CreateDraftFromBook(_store, projectId);
        Assert.True(r.Ok, r.Error);
        var text = ScreenplayService.Get(_store, projectId).Text;
        Assert.Contains("Title:", text);
        Assert.DoesNotContain("= page ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[[page ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("naps", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripBookPageTags_removes_synopsis_and_note_forms()
    {
        var raw = """
            Title: X

            INT. ROOM - DAY
            = page 2
            [[page 2]]

            Action here.
            """;
        var cleaned = AdaptationFountain.StripBookPageTags(raw);
        Assert.DoesNotContain("= page", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[[page", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INT. ROOM", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Action here", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Book_context_resolves_synopsis_and_note_page_tags()
    {
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "book_full.txt"),
            "--- PAGE 1 ---\nAlpha page.\n\n--- PAGE 2 ---\nBeta bedtime page.\n");

        var ctx = await BookContextService.GetContextAsync(
            _store, projectId,
            sceneIndex: 1,
            sceneHeading: "INT. BEDROOM - NIGHT",
            sceneBody: "= page 2\n\n[[page 2]]\n\nNARRATOR\nHe slept.\n");
        Assert.True(ctx.HasBook);
        Assert.Equal(2, ctx.PageNumber);
        Assert.Contains("bedtime", ctx.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikeGoodFountain_accepts_real_structure_rejects_dump()
    {
        var good = """
            Title: Buster
            Draft date: 1/1/2026

            INT. LIVING ROOM - EVENING

            = page 2

            [[page 2]]

            NARRATOR
            He's a small dog.

            MOMMA
            Time for bed!
            """;
        Assert.True(AdaptationFountain.LooksLikeGoodFountain(good));

        var dump = """
            Title: X
            INT. STORY - PAGE 1 - DAY
            text
            INT. STORY - PAGE 2 - DAY
            more
            INT. STORY - PAGE 3 - DAY
            more
            """;
        Assert.False(AdaptationFountain.LooksLikeGoodFountain(dump));
    }

    [Fact]
    public async Task BuildSystemPrompt_uses_book_to_fountain_with_stage1_learnings()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true); // soft-pass when prompts not on disk
            return;
        }

        var fountainPath = Path.Combine(root, "prompts", "book_to_fountain.txt");
        Assert.True(File.Exists(fountainPath), "Expected prompts/book_to_fountain.txt");

        var system = await AdaptationFountain.BuildSystemPromptAsync(12);

        // Fountain product path — not the JSON schema dump
        Assert.Contains("Fountain", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page numbers", system, StringComparison.OrdinalIgnoreCase); // forbid page tags
        Assert.Contains("12", system); // TOTAL_RUNTIME substituted
        Assert.DoesNotContain("{{TOTAL_RUNTIME_MINUTES}}", system);
        Assert.DoesNotContain("stage1_scene_bible.schema.json", system);

        // Key learnings carried over from the old Stage 1 prompt
        Assert.Contains("VO", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed cast", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fidelity", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NARRATOR", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INT.", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wardrobe", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visual medium", system, StringComparison.OrdinalIgnoreCase); // v4 wording
        Assert.Contains("spoiler", system, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoWithPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "book_to_fountain.txt");
            if (File.Exists(candidate))
                return dir.FullName;
        }
        var known = @"C:\Users\budcr\source\repos\NickAndMe";
        if (File.Exists(Path.Combine(known, "prompts", "book_to_fountain.txt")))
            return known;
        return null;
    }

    [Fact]
    public void Adaptation_status_next_step_sign_screenplay_when_draft_dirty()
    {
        const string projectId = "Demo";
        ScreenplayService.SaveDraft(_store, projectId, "INT. A - DAY\n\nX.\n");
        var status = _store.GetAdaptationStatus(projectId);
        Assert.Equal("sign_screenplay", status.NextStep);
        Assert.True(status.Screenplay.DraftExists);
        Assert.False(status.Screenplay.Dirty); // unsigned draft, not post-approval edit
        Assert.False(status.Screenplay.Signed);
    }

    [Fact]
    public void EnsureCanonicalDraft_adopts_imported_named_fountain()
    {
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        var named = Path.Combine(source, "Brick-And-Steel.fountain");
        File.WriteAllText(named, "Title: Brick\n\nINT. PATIO - DAY\n\nSun.\n");

        var adopted = ScreenplayService.EnsureCanonicalDraft(_store, projectId);
        Assert.True(adopted);
        var canonical = ScreenplayService.GetDraftPath(_store, projectId);
        Assert.True(File.Exists(canonical));
        Assert.Contains("PATIO", File.ReadAllText(canonical), StringComparison.OrdinalIgnoreCase);

        var doc = ScreenplayService.Get(_store, projectId);
        Assert.True(doc.Status.DraftExists);
        Assert.Contains("PATIO", doc.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BookTextToFountainDraft_delegates_to_stub()
    {
        var f = ScreenplayService.BookTextToFountainDraft("My Book",
            "--- PAGE 1 ---\nA little dog naps by the warm fire tonight.\n");
        Assert.Contains("Title:", f);
        Assert.Contains("naps", f, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[[page ", f, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChunkBookForAdaptation_splits_long_chaptered_text()
    {
        var sb = new System.Text.StringBuilder();
        for (var c = 1; c <= 12; c++)
        {
            sb.Append("CHAPTER ").Append(c).Append('\n');
            sb.Append(new string('a', 3_000)).Append(" chapter body ").Append(c).Append("\n\n");
        }
        var book = sb.ToString();
        Assert.True(book.Length > AdaptationFountain.SingleShotMaxChars);

        var chunks = AdaptationFountain.ChunkBookForAdaptation(book);
        Assert.InRange(chunks.Count, 2, AdaptationFountain.MaxAdaptChunks);
        Assert.True(chunks.Sum(c => c.Length) >= book.Length * 0.9);
        // First chunk should open near chapter 1
        Assert.Contains("CHAPTER 1", chunks[0], StringComparison.OrdinalIgnoreCase);
        // Last chunk should include late material
        Assert.Contains("CHAPTER", chunks[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StitchFountainParts_keeps_one_title_page_and_all_scenes()
    {
        var p1 = """
            Title: Epic
            Author: Test
            Draft date: 1/1/2026

            INT. CASTLE - DAY

            HERO
            Hello.
            """;
        var p2 = """
            Title: Epic
            Author: Test

            EXT. FOREST - NIGHT

            HERO
            Again.

            FADE OUT.

            THE END
            """;
        var stitched = AdaptationFountain.StitchFountainParts(new[] { p1, p2 });
        Assert.Single(Regex.Matches(stitched, @"(?im)^Title:"));
        Assert.Contains("INT. CASTLE", stitched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXT. FOREST", stitched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THE END", stitched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixDraftDate_overwrites_model_invented_year()
    {
        var raw = """
            Title: Test
            Draft date: 3/25/2025

            INT. ROOM - DAY

            NARRATOR
            Hello world this is long enough for structural gates and more text.
            """;
        var fixedText = AdaptationFountain.FixDraftDate(raw);
        var today = DateTime.Now.ToString("M/d/yyyy");
        Assert.Contains($"Draft date: {today}", fixedText, StringComparison.Ordinal);
        Assert.DoesNotContain("3/25/2025", fixedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INT. OLD HOUSE - VARIOUS ROOMS - NIGHT", true)]
    [InlineData("INT. HOUSE - VARIOUS - NIGHT", true)]
    [InlineData("INT. MULTIPLE LOCATIONS - NIGHT", true)]
    [InlineData("INT. HALL AND SITTING ROOM - NIGHT", false)]
    [InlineData("INT. OLD MAN'S CHAMBER - NIGHT", false)]
    public void HeadingContainsVagueLocationLanguage_detects_fillers(string heading, bool expected)
    {
        Assert.Equal(expected, AdaptationFountain.HeadingContainsVagueLocationLanguage(heading));
    }

    [Fact]
    public void FindVagueLocationHeadings_finds_various_rooms()
    {
        var fountain = """
            Title: T

            INT. BARE ROOM - NIGHT

            NARRATOR
            Hello there this is enough body text for a cue.

            INT. OLD HOUSE - VARIOUS ROOMS - NIGHT

            NARRATOR (V.O.)
            We walk about.

            INT. OLD MAN'S CHAMBER - NIGHT

            NARRATOR
            Here.
            """;
        var bad = AdaptationFountain.FindVagueLocationHeadings(fountain);
        Assert.Single(bad);
        Assert.Contains("VARIOUS", bad[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("FIRST OFFICER", true)]
    [InlineData("SECOND OFFICER", true)]
    [InlineData("THIRD OFFICER", true)]
    [InlineData("FIRST BUSINESSMAN", true)]
    [InlineData("SECOND MERCHANT", true)]
    [InlineData("THIRD MERCHANT", true)]
    [InlineData("OFFICER 1", true)]
    [InlineData("POLICE OFFICER #2", true)]
    [InlineData("GUEST 3", true)]
    [InlineData("MAN 2", true)]
    [InlineData("OFFICER REYNOLDS", false)]
    [InlineData("MR. TOPPER", false)]
    [InlineData("NARRATOR", false)]
    [InlineData("OLD MAN", false)]
    [InlineData("SCROOGE", false)]
    public void IsGenericNumberedSpeaker_detects_ordinals(string name, bool expected)
    {
        Assert.Equal(expected, AdaptationFountain.IsGenericNumberedSpeaker(name));
    }

    [Theory]
    [InlineData("picture_book", 20)]
    [InlineData("short", 22)]
    [InlineData("novel", 45)]
    public void SoftMaxSceneHeadings_by_book_kind(string kind, int expected)
    {
        Assert.Equal(expected, AdaptationFountain.SoftMaxSceneHeadings(kind));
    }

    [Fact]
    public void FindGenericNumberedSpeakers_lists_ordinal_cues()
    {
        var fountain = """
            Title: T

            INT. DOOR - NIGHT

            FIRST OFFICER
            Open up.

            SECOND OFFICER
            Search.

            OFFICER REYNOLDS
            Already named.
            """;
        var found = AdaptationFountain.FindGenericNumberedSpeakers(fountain);
        Assert.Contains(found, n => n.Equals("FIRST OFFICER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, n => n.Equals("SECOND OFFICER", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, n => n.Contains("REYNOLDS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindLocationDriftCandidateGroups_clusters_shared_first_word()
    {
        var fountain = """
            Title: T

            INT. SIONNA'S HOUSE - NIGHT

            NARRATOR
            First visit.

            EXT. SIONNA'S DUPLEX - DUSK

            NARRATOR
            Different wording, same place.

            EXT. SIONNA'S OLD DUPLEX - DAY

            NARRATOR
            Years later.

            INT. JOE'S BAR - NIGHT

            NARRATOR
            Unrelated place, only one heading — must not appear as a candidate.
            """;
        var groups = AdaptationFountain.FindLocationDriftCandidateGroups(fountain);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
        Assert.Contains(groups[0], h => h.Equals("SIONNA'S HOUSE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups[0], h => h.Equals("SIONNA'S DUPLEX", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups[0], h => h.Equals("SIONNA'S OLD DUPLEX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(groups[0], h => h.Contains("JOE'S", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindLocationDriftCandidateGroups_ignores_short_shared_words_and_singletons()
    {
        var fountain = """
            Title: T

            INT. THE OLD BARN - DAY

            NARRATOR
            One.

            INT. THE NEW BARN - NIGHT

            NARRATOR
            Two — shares only the short word "THE", must not cluster on that alone.

            INT. LIBRARY - DAY

            NARRATOR
            Only heading with this first word.
            """;
        var groups = AdaptationFountain.FindLocationDriftCandidateGroups(fountain);
        Assert.Empty(groups);
    }

    [Fact]
    public void FindNameDriftCandidateGroups_finds_cue_and_prose_spelling_drift()
    {
        var fountain = """
            Title: T

            INT. KIRK STREET APARTMENT - DAY

            PETER (V.O.)
            Mother: Olivia Anne Olsen. She kept the faith.

            NICK
            Mrs. Olivia Olson was a good woman.

            SIONNA
            Unrelated person, appears once, must not cluster.
            """;
        var groups = AdaptationFountain.FindNameDriftCandidateGroups(fountain);
        Assert.Contains(groups, g =>
            g.Any(n => n.Equals("Olsen", StringComparison.OrdinalIgnoreCase)) &&
            g.Any(n => n.Equals("Olson", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(groups, g => g.Any(n => n.Equals("Sionna", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("Olsen", "Olson", true)]
    [InlineData("Sionna", "Sienna", true)]
    [InlineData("Nick", "Peter", false)]
    [InlineData("Bob", "Rob", false)] // below the 4-char floor
    [InlineData("Nick", "Nick", false)] // identical is not drift
    public void IsNameSpellingDriftCandidate_detects_close_but_not_identical(string a, string b, bool expected)
    {
        Assert.Equal(expected, AdaptationFountain.IsNameSpellingDriftCandidate(a, b));
    }

    [Fact]
    public void NormalizeSceneHeadingWording_unifies_prefixed_hallway()
    {
        var fountain = """
            Title: T

            INT. OLD HOUSE - HALL OUTSIDE CHAMBER - NIGHT

            NARRATOR
            Outside first.

            INT. HALL OUTSIDE CHAMBER - NIGHT

            NARRATOR
            Outside again.

            INT. OLD MAN'S CHAMBER - NIGHT

            NARRATOR
            Inside.
            """;
        var norm = AdaptationFountain.NormalizeSceneHeadingWording(fountain);
        var halls = Regex.Matches(norm, @"(?im)^(INT\..*HALL OUTSIDE CHAMBER.*)$")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(halls.Count == 1, "expected one hallway wording, got: " + string.Join(" | ", halls));
        Assert.DoesNotContain("OLD HOUSE - HALL", norm, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INT. HALL OUTSIDE CHAMBER - NIGHT", norm, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INT. OLD MAN'S CHAMBER - NIGHT", norm, StringComparison.OrdinalIgnoreCase);
    }

    // ── split narration (continuous V.O./verse broken by a real blank line) ───────────────

    // Two spaces = the Fountain line-break convention for a stanza break inside dialogue.
    private const string TwoSpaceBreak = "  ";

    private static string JoinFountain(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void FindSplitNarrationBlocks_flags_verse_split_by_real_blank()
    {
        // Multi-stanza V.O. poem where stanzas are separated by a REAL blank line — per the
        // Fountain spec the blank ends the dialogue block, demoting stanzas 2+ to silent action.
        var fountain = JoinFountain(
            "Title: T",
            "",
            "INT. FIELD - DAY",
            "",
            "NARRATOR (V.O.)",
            "Mary had a little lamb,",
            "its fleece was white as snow.",
            "",
            "And everywhere that Mary went,",
            "the lamb was sure to go.",
            "",
            "FADE OUT.");

        var split = AdaptationFountain.FindSplitNarrationBlocks(fountain);
        Assert.Single(split);
        Assert.Equal("NARRATOR", split[0].CueName);
        Assert.Contains("V.O", split[0].CueExtension, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(split[0].OrphanActionLines, l => l.StartsWith("And everywhere", StringComparison.Ordinal));
    }

    [Fact]
    public void FindSplitNarrationBlocks_ignores_correctly_written_verse()
    {
        // Same poem written correctly: one block, stanzas joined by a two-space line break.
        var fountain = JoinFountain(
            "Title: T",
            "",
            "INT. FIELD - DAY",
            "",
            "NARRATOR (V.O.)",
            "Mary had a little lamb,",
            "its fleece was white as snow.",
            TwoSpaceBreak,
            "And everywhere that Mary went,",
            "the lamb was sure to go.",
            "",
            "FADE OUT.");

        Assert.Empty(AdaptationFountain.FindSplitNarrationBlocks(fountain));
        // And the re-merge is a no-op on already-correct verse.
        Assert.Equal(fountain, AdaptationFountain.RemergeSplitNarration(fountain));
    }

    [Fact]
    public void FindSplitNarrationBlocks_ignores_prose_action_after_vo()
    {
        // Ordinary prose action after a V.O. line must NOT flag: the action side is not verse-shaped
        // (long flowing sentences), and requiring BOTH sides to be verse is what keeps this quiet.
        var fountain = JoinFountain(
            "Title: T",
            "",
            "INT. FIELD - DAY",
            "",
            "NARRATOR (V.O.)",
            "It was the coldest winter that anyone in the valley could remember.",
            "",
            "Snow drifts pile against the barn door as the wind howls across the empty pasture.",
            "The old farmhouse stands dark, a single lamp burning behind a frosted window.",
            "",
            "FADE OUT.");

        Assert.Empty(AdaptationFountain.FindSplitNarrationBlocks(fountain));
    }

    [Fact]
    public void RemergeSplitNarration_merges_all_stanzas_into_one_dialogue_block()
    {
        // Three-stanza poem split by real blank lines: the deterministic fallback must pull every
        // orphaned verse stanza back under the single NARRATOR (V.O.) cue as spoken dialogue.
        var stanzaLines = new[]
        {
            "Mary had a little lamb,",
            "its fleece was white as snow.",
            "And everywhere that Mary went,",
            "the lamb was sure to go.",
            "It followed her to school one day,",
            "which was against the rule.",
        };
        var fountain = JoinFountain(
            "Title: T",
            "",
            "INT. FIELD - DAY",
            "",
            "NARRATOR (V.O.)",
            stanzaLines[0],
            stanzaLines[1],
            "",
            stanzaLines[2],
            stanzaLines[3],
            "",
            stanzaLines[4],
            stanzaLines[5],
            "",
            "FADE OUT.");

        var merged = AdaptationFountain.RemergeSplitNarration(fountain);

        // The detector no longer fires on the corrected text.
        Assert.Empty(AdaptationFountain.FindSplitNarrationBlocks(merged));

        // Parse with the real Fountain parser: every stanza line is Dialogue, none is Action,
        // and the poem stays under exactly one voice-over cue.
        var parsed = FountainParser.Parse(merged);
        var dialogue = parsed.Elements
            .Where(e => e.Type == FountainParser.ElementType.Dialogue)
            .Select(e => e.Text)
            .ToList();
        var action = parsed.Elements
            .Where(e => e.Type == FountainParser.ElementType.Action)
            .Select(e => e.Text)
            .ToList();

        foreach (var line in stanzaLines)
        {
            Assert.Contains(line, dialogue);
            Assert.DoesNotContain(line, action);
        }

        var (voCues, _) = FountainParser.CountVoiceoverCues(merged);
        Assert.Equal(1, voCues);
    }

    // NOTE: the "bare closing moral with no cue" case is prompt-driven only (there is no cue to
    // re-merge into), so it is exercised by the prompt rule in book_to_fountain.txt, not here.

    [Fact]
    public void IsLocationNameAlias_detects_prefix_drift()
    {
        Assert.True(AdaptationFountain.IsLocationNameAlias(
            "OLD HOUSE - HALL OUTSIDE CHAMBER", "HALL OUTSIDE CHAMBER"));
        Assert.False(AdaptationFountain.IsLocationNameAlias(
            "OLD MAN'S CHAMBER", "CHAMBER"));
        Assert.False(AdaptationFountain.IsLocationNameAlias(
            "HALL", "HALL OUTSIDE CHAMBER"));
    }

    [Fact]
    public void LooksLikeGoodFountain_allows_novels_without_page_tags()
    {
        var novel = """
            Title: Novel
            Author: A

            INT. DRAWING ROOM - NIGHT

            NARRATOR
            It was a dark and stormy night, longer than one hundred and twenty characters of padding for the quality gate to pass structural validation of the fountain body text.

            HERO
            We begin.
            """;
        Assert.True(AdaptationFountain.LooksLikeGoodFountain(novel));
        // requirePageTags is ignored (page tags stripped / not required)
        Assert.True(AdaptationFountain.LooksLikeGoodFountain(novel, requirePageTags: true));
    }

    [Fact]
    public void LooksLikeGoodFountain_accepts_picture_book_shape_after_page_tag_strip()
    {
        var raw = """
            Title: Buster
            Author: Debra

            EXT. YARD - DAY
            = page 2
            [[page 2]]

            NARRATOR
            He's Buster the Noodle Head Dog.
            """;
        Assert.True(AdaptationFountain.LooksLikeGoodFountain(raw));
        var stripped = AdaptationFountain.StripBookPageTags(raw);
        Assert.DoesNotContain("= page", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.True(AdaptationFountain.LooksLikeGoodFountain(stripped));
    }

    [Fact]
    public async Task Stage2_plans_from_approved_fountain_without_scenes_json()
    {
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        var fountain = """
            Title: Clip Test

            INT. KITCHEN - DAY

            A dog watches toast.

            MOM
            Breakfast is ready.

            EXT. YARD - DAY

            The dog runs.
            """;
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);
        Assert.False(File.Exists(_store.ResolveScenesJsonPath(projectId)));

        var planner = new Stage2PlannerService(
            _store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "720p", scenes: "all");
        Assert.True(result.Ok);
        Assert.True(result.SceneCount >= 2);
        Assert.True(result.ClipCount >= 1);
        Assert.True(File.Exists(result.OutPath));
        var bp = File.ReadAllText(result.OutPath!);
        Assert.Contains("screenplay.fountain", bp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("veo_clips", bp, StringComparison.OrdinalIgnoreCase);
    }
}

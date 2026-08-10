using PageToMovie.Adaptation;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class AdaptationDensityTests
{
    [Fact]
    public void Mary_natural_is_about_one_to_two_minutes_high_density()
    {
        var book = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MaryHadALittleLamb.txt"));
        var e = AdaptationDensity.EstimateNatural(book);

        // Natural storybook read-aloud (2.5 wps) × staging: a 136-word rhyme is ~1–2 min of film,
        // not the ~3 the old 1.15-wps rate produced.
        Assert.InRange(e.NaturalFilmMinutes, 1, 2);
        Assert.Equal("verse_speech_x_staging", e.Method);
        Assert.True(e.MinutesPerThousandWords > 5, $"δ={e.MinutesPerThousandWords}");
        Assert.Null(AdaptationDensity.SuggestReducedBenchmarkMinutes(e));
    }

    [Fact]
    public void Tell_Tale_Heart_calibrates_near_published_seventeen_minutes()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "books", "The_Tell-Tale_Heart.txt"));
        Assert.True(File.Exists(path), $"Missing fixture book at {path}");

        var e = AdaptationDensity.EstimateNatural(File.ReadAllText(path));
        // Published PageToMovie TTH on YouTube is 16:49; density should land near that, not ~10.
        Assert.Equal(BookKind.Short, e.BookKind);
        Assert.Equal("short_literary_speech_x_staging", e.Method);
        Assert.InRange(e.NaturalFilmMinutes, 14, 20);
        Assert.InRange(e.MinutesPerThousandWords, 6.0, 10.0);
        Assert.Null(AdaptationDensity.SuggestReducedBenchmarkMinutes(e));
    }

    [Fact]
    public void Nick_scale_novel_lands_feature_band_not_audiobook()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "books", "Nick_and_Me.txt"));
        if (!File.Exists(path))
        {
            var synth = string.Join(' ', Enumerable.Repeat("Nick walked home and thought about the day.", 6000));
            var eSynth = AdaptationDensity.EstimateNatural(synth, bookKind: BookKind.Novel);
            Assert.InRange(eSynth.NaturalFilmMinutes, 40, 180);
            Assert.True(eSynth.TemporalCompressionRatio < 0.6);
            return;
        }

        var e = AdaptationDensity.EstimateNatural(File.ReadAllText(path));
        Assert.Equal(BookKind.Novel, e.BookKind);
        Assert.InRange(e.NaturalFilmMinutes, 80, 180);
        Assert.True(e.AudiobookMinutes > 300, "Nick should be multi-hour as audiobook");
        Assert.True(e.TemporalCompressionRatio < 0.5, $"τ={e.TemporalCompressionRatio}");
        Assert.InRange(e.MinutesPerThousandWords, 1.2, 3.5);

        var reduced = AdaptationDensity.SuggestReducedBenchmarkMinutes(e);
        Assert.NotNull(reduced);
        Assert.True(reduced < e.NaturalFilmMinutes);
        Assert.InRange(reduced!.Value, 20, e.NaturalFilmMinutes - 5);
    }

    [Fact]
    public void Density_definition_is_minutes_per_thousand_words()
    {
        var e = AdaptationDensity.EstimateNatural(
            string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 200)),
            bookKind: BookKind.Short);
        var expected = e.NaturalFilmMinutes / (e.SourceWords / 1000.0);
        Assert.Equal(Math.Round(expected, 2), e.MinutesPerThousandWords);
    }

    [Fact]
    public void Stage1_resolve_matches_density_natural()
    {
        var book = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MaryHadALittleLamb.txt"));
        var density = AdaptationDensity.EstimateNatural(book).NaturalFilmMinutes;
        Assert.Equal(density, BookTextAnalyzer.ResolveStage1RuntimeMinutes(book));
        Assert.Equal(density, BookTextAnalyzer.Analyze(book).SuggestedTotalMinutes);
    }
}

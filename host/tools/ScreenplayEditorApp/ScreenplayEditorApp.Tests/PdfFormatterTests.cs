using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class PdfFormatterTests
{
    private static string GetFixturePath(string relativeOrFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "PageToMovie.Tests", "Fixtures", relativeOrFileName);
            if (File.Exists(candidate))
                return candidate;

            var hostCandidate = Path.Combine(dir.FullName, "host", "PageToMovie.Tests", "Fixtures", relativeOrFileName);
            if (File.Exists(hostCandidate))
                return hostCandidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Fixture file '{relativeOrFileName}' could not be located from '{AppContext.BaseDirectory}'.");
    }

    private static ScreenplayModel BuildSampleModel()
    {
        var model = new ScreenplayModel();

        var kitchen = new ScreenplayScene { SceneTitle = "INT. KITCHEN - DAY" };
        kitchen.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "JANE stands at the counter, chopping vegetables with quick, practiced strokes."
        });
        kitchen.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            Speaker = "Jane",
            Parenthetical = "without looking up",
            SpokenText = "You're going to be late again if you don't hurry up."
        });
        kitchen.Beats.Add(new ScreenplayBeat { BeatType = BeatType.Transition, TransitionText = "CUT TO:" });
        model.Scenes.Add(kitchen);

        var porch = new ScreenplayScene { SceneTitle = "EXT. FRONT PORCH - MOMENTS LATER" };
        porch.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "TOMMY bursts through the door, one shoe still untied."
        });
        porch.Beats.Add(new ScreenplayBeat { BeatType = BeatType.Dialogue, Speaker = "Tommy", Extension = "O.S.", SpokenText = "Coming!" });
        porch.Beats.Add(new ScreenplayBeat { BeatType = BeatType.Note, ActionText = "this production note should never appear in the pdf" });
        model.Scenes.Add(porch);

        return model;
    }

    /// <summary>
    /// PdfFormatter writes uncompressed content streams with plain "(text) Tj" operators, so
    /// literal strings can be recovered without any third-party PDF-reading dependency.
    /// </summary>
    private static List<string> ExtractPdfLiteralStrings(byte[] pdfBytes)
    {
        var result = new List<string>();
        var text = Encoding.Latin1.GetString(pdfBytes);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '(')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != ')')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }
                    sb.Append(text[i]);
                    i++;
                }
                result.Add(sb.ToString());
            }
            i++;
        }
        return result;
    }

    [Fact]
    public void TestToPdfBytes_DoesNotThrow_OnRealMultiSceneScreenplay()
    {
        var fixturePath = GetFixturePath("BookToFountainPackage/fountain_adaptations/07_The_Tell-Tale_Heart.fountain");
        var fountainText = File.ReadAllText(fixturePath);
        var model = FountainFormatter.Parse(fountainText);
        Assert.True(model.Scenes.Count > 1);

        var bytes = model.ToPdfBytes();

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void TestToPdfBytes_ProducesValidPdfBytes()
    {
        var model = BuildSampleModel();

        var bytes = model.ToPdfBytes();

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        var tail = Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 16), Math.Min(16, bytes.Length));
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public void TestToPdfBytes_HandlesEmptyModel()
    {
        var bytes = new ScreenplayModel().ToPdfBytes();

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void TestToPdfBytes_ContainsSceneHeadingsAndCharacterNames_AndExcludesNotes()
    {
        var model = BuildSampleModel();

        var bytes = model.ToPdfBytes();
        var joined = string.Join(" ", ExtractPdfLiteralStrings(bytes));

        Assert.Contains("KITCHEN", joined);
        Assert.Contains("FRONT PORCH", joined);
        Assert.Contains("JANE", joined);
        Assert.Contains("TOMMY", joined);
        Assert.Contains("CUT TO:", joined);
        Assert.DoesNotContain("production note", joined);
    }

    [Fact]
    public void TestToPdfBytes_PaginatesAndNumbersPagesAfterTheFirst()
    {
        var model = new ScreenplayModel();
        for (var i = 0; i < 40; i++)
        {
            var scene = new ScreenplayScene { SceneTitle = $"INT. ROOM {i} - DAY" };
            scene.Beats.Add(new ScreenplayBeat
            {
                BeatType = BeatType.Action,
                ActionText = "Filler action text to consume vertical space across many lines so layout reliably crosses a page boundary."
            });
            scene.Beats.Add(new ScreenplayBeat { BeatType = BeatType.Dialogue, Speaker = $"CHARACTER{i}", SpokenText = "Some spoken dialogue to fill space on the page." });
            model.Scenes.Add(scene);
        }

        var bytes = model.ToPdfBytes();
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("/Type /Pages", text);
        Assert.Contains("(2.)", text);
        Assert.DoesNotContain("(1.)", text);
    }
}

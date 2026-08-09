using System;
using System.Collections.Generic;
using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class ScreenplayModelTests
{
    [Fact]
    public void TestScreenplayMetadataDefaults()
    {
        var meta = new ScreenplayMetadata();
        Assert.Equal("UNTITLED SCREENPLAY", meta.Title);
        Assert.Equal("", meta.Author);
        Assert.Equal("Written by", meta.Credit);
        Assert.Equal("", meta.Source);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), meta.DraftDate);
        Assert.Equal("", meta.Contact);
        Assert.Equal("", meta.Notes);
    }

    [Fact]
    public void TestScreenplaySceneHeaderTextFormatting()
    {
        var scene = new ScreenplayScene
        {
            SceneNumber = 1,
            Environment = "INT.",
            Location = "LIVING ROOM",
            TimeOfDay = "DAY"
        };

        Assert.Equal("INT. LIVING ROOM - DAY", scene.HeaderText);

        scene.Location = "PARK";
        scene.Environment = "EXT.";
        scene.TimeOfDay = "NIGHT";
        Assert.Equal("EXT. PARK - NIGHT", scene.HeaderText);
    }

    [Fact]
    public void TestScreenplayBeatTypesAndProperties()
    {
        var actionBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Action,
            ActionText = "Visual description here."
        };
        Assert.Equal(BeatType.Action, actionBeat.BeatType);
        Assert.Equal("Visual description here.", actionBeat.ActionText);

        var dialogueBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            Speaker = "HERO",
            Extension = "V.O.",
            Parenthetical = "quietly",
            SpokenText = "We must go."
        };
        Assert.Equal(BeatType.Dialogue, dialogueBeat.BeatType);
        Assert.Equal("HERO", dialogueBeat.Speaker);
        Assert.Equal("V.O.", dialogueBeat.Extension);
        Assert.Equal("quietly", dialogueBeat.Parenthetical);
        Assert.Equal("We must go.", dialogueBeat.SpokenText);

        var transBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Transition,
            TransitionText = "FADE OUT."
        };
        Assert.Equal(BeatType.Transition, transBeat.BeatType);
        Assert.Equal("FADE OUT.", transBeat.TransitionText);
    }

    [Fact]
    public void TestModelToFountainAndParseEmpty()
    {
        var emptyModel = new ScreenplayModel();
        string output = emptyModel.ToFountain();
        Assert.Contains("UNTITLED SCREENPLAY", output);

        var reParsed = FountainFormatter.Parse(output);
        Assert.NotNull(reParsed.Metadata);
        Assert.Equal("UNTITLED SCREENPLAY", reParsed.Metadata.Title);
    }
}

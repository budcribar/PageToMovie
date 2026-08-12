using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Validation;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CastPackageCrossCheckTests
{
    private const string MaryBook = """
        Mary had a little lamb,
        Its fleece was white as snow.
        And everywhere that Mary went,
        The lamb was sure to go.
        He followed her to school one day,
        That was against the rule.
        It made the children laugh and play
        To see a lamb at school.
        And so the teacher turned him out,
        But still he lingered near.
        """;

    private const string MaryFountain = """
        Title: MARY HAD A LITTLE LAMB

        FADE IN:

        EXT. COUNTRY LANE - DAY

        MARY, a young girl with brown braids, walks with her LAMB.

        MARY
        Come along.

        INT. SCHOOLHOUSE - DAY

        The TEACHER stands at the front. ELI and CLARA watch the lamb.

        ELI
        Why does he follow her?

        CLARA
        What makes the lamb love Mary so?

        TEACHER
        Oh, Mary loves the lamb, you know.

        FADE OUT.
        """;

    private const string MaryGroupFountain = """
        Title: MARY HAD A LITTLE LAMB

        FADE IN:

        EXT. SCHOOLYARD - DAY

        MARY stands with her LAMB. CHILDREN gather around.

        CHILDREN
        What makes the lamb love Mary so?

        TEACHER
        Oh, Mary loves the lamb, you know.

        FADE OUT.
        """;

    private const string FullCast = """
        {
          "schema_version": "cast_seeds.v1",
          "character_seed_tokens": {
            "Character_Mary": {
              "canonical_given_name": "Mary",
              "description": "A young girl with brown braids, a blue pinafore, white apron, and straw bonnet.",
              "visual_lock": "brown braids, blue pinafore, white apron, straw bonnet, school-age girl",
              "wardrobe_lock": "blue pinafore, white apron, straw bonnet",
              "species_kind": "human",
              "display_name_policy": "ok_anytime"
            },
            "Character_Eli": {
              "canonical_given_name": "Eli",
              "description": "A freckled boy about eight in a brown waistcoat over a cream shirt.",
              "visual_lock": "freckled boy, short auburn hair, brown waistcoat",
              "wardrobe_lock": "brown waistcoat, cream shirt, dark trousers",
              "species_kind": "human",
              "display_name_policy": "ok_anytime"
            },
            "Character_Clara": {
              "canonical_given_name": "Clara",
              "description": "A girl about eight with dark curls tied with a yellow ribbon.",
              "visual_lock": "dark shoulder-length curls, yellow ribbon",
              "wardrobe_lock": "muted green pinafore, white blouse",
              "species_kind": "human",
              "display_name_policy": "ok_anytime"
            },
            "Character_Teacher": {
              "canonical_given_name": "Teacher",
              "description": "A middle-aged woman in a dark gray dress with a white collar and pinned brown hair.",
              "visual_lock": "middle-aged woman, pinned brown hair, dark gray dress, white collar",
              "species_kind": "human",
              "display_name_policy": "ok_anytime"
            },
            "Character_Lamb": {
              "canonical_given_name": "Lamb",
              "description": "A small lamb with snowy fleece and a red ribbon at its neck.",
              "visual_lock": "snowy fleece, red neck ribbon, small lamb",
              "species_kind": "animal",
              "display_name_policy": "ok_anytime"
            }
          }
        }
        """;

    private const string GroupCast = """
        {
          "schema_version": "cast_seeds.v1",
          "character_seed_tokens": {
            "Character_Mary": {
              "canonical_given_name": "Mary",
              "description": "A young girl with brown braids, a blue pinafore, white apron, and straw bonnet.",
              "visual_lock": "brown braids, blue pinafore, white apron, straw bonnet, school-age girl",
              "wardrobe_always": ["blue pinafore"],
              "species_kind": "human"
            },
            "Character_Children": {
              "canonical_given_name": "Children",
              "description": "A small group of school-age children in simple period play clothes.",
              "visual_lock": "several young schoolchildren, eager faces, mixed hair colors",
              "species_kind": "human"
            },
            "Character_Teacher": {
              "canonical_given_name": "Teacher",
              "description": "A middle-aged woman in a dark gray dress with pinned brown hair.",
              "visual_lock": "middle-aged woman, pinned brown hair, dark gray dress",
              "species_kind": "human"
            }
          }
        }
        """;

    [Fact]
    public void Speakers_include_named_children_and_teacher()
    {
        var speakers = CastPackageCrossCheck.ExtractSpeakers(MaryFountain);
        Assert.Contains("MARY", speakers);
        Assert.Contains("ELI", speakers);
        Assert.Contains("CLARA", speakers);
        Assert.Contains("TEACHER", speakers);
    }

    [Fact]
    public void Full_cast_package_scores_high()
    {
        var report = CastPackageCrossCheck.Evaluate(MaryFountain, FullCast);
        Assert.True(report.Ok, string.Join("; ", report.Failures));
        Assert.True(report.Score >= 85, $"score={report.Score}");
        Assert.Contains("Character_Eli", report.MatchedKeys);
        Assert.Contains("Character_Clara", report.MatchedKeys);
    }

    [Fact]
    public void Missing_cast_file_fails_hard()
    {
        var report = CastPackageCrossCheck.Evaluate(MaryFountain, castSeedsJson: null);
        Assert.False(report.Ok);
        Assert.Equal(0, report.Score);
        Assert.Contains(report.Failures, f => f.Contains("cast_seeds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Partial_cast_missing_children_fails_membership()
    {
        var partial = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Mary": {
                  "canonical_given_name": "Mary",
                  "description": "A young girl with brown braids and a blue pinafore.",
                  "visual_lock": "brown braids, blue pinafore",
                  "species_kind": "human",
                  "display_name_policy": "ok_anytime"
                }
              }
            }
            """;
        var report = CastPackageCrossCheck.Evaluate(MaryFountain, partial);
        Assert.False(report.Ok);
        Assert.Contains(report.Failures, f => f.Contains("ELI", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Failures, f => f.Contains("CLARA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Failures, f => f.Contains("TEACHER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Invented_names_flagged_when_book_provided()
    {
        var report = CastPackageCrossCheck.Evaluate(MaryFountain, FullCast, MaryBook);
        Assert.Contains("ELI", report.SpeakersMissingFromBook);
        Assert.Contains("CLARA", report.SpeakersMissingFromBook);
        Assert.DoesNotContain("MARY", report.SpeakersMissingFromBook);
        Assert.DoesNotContain("TEACHER", report.SpeakersMissingFromBook);
        Assert.Contains(report.Warnings, w => w.Contains("ELI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Group_children_token_does_not_flag_invented_names()
    {
        var report = CastPackageCrossCheck.Evaluate(MaryGroupFountain, GroupCast, MaryBook);
        Assert.True(report.Ok, string.Join("; ", report.Failures));
        Assert.Empty(report.SpeakersMissingFromBook);
        Assert.Contains("Character_Children", report.MatchedKeys);
    }

    [Fact]
    public void Facade_CrossCheckCast_matches_static_Evaluate()
    {
        var viaFacade = new AdaptationService().CrossCheckCast(MaryGroupFountain, GroupCast, MaryBook);
        var viaStatic = CastPackageCrossCheck.Evaluate(MaryGroupFountain, GroupCast, MaryBook);
        Assert.Equal(viaStatic.Ok, viaFacade.Ok);
        Assert.Equal(viaStatic.Score, viaFacade.Score);
        Assert.Equal(viaStatic.MatchedKeys, viaFacade.MatchedKeys);
    }

    [Fact]
    public void SpeakersMissingFromCast_lists_eli_clara_when_only_group_seed()
    {
        var cast = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Mary": {
                  "canonical_given_name": "Mary",
                  "description": "A young girl with brown braids and a blue pinafore over a white apron.",
                  "visual_lock": "brown braids, blue pinafore",
                  "species_kind": "human"
                },
                "Character_Teacher": {
                  "canonical_given_name": "Teacher",
                  "description": "Adult woman in plain gray dress with hair in a bun.",
                  "visual_lock": "gray dress, hair in a bun",
                  "species_kind": "human"
                },
                "Character_Children": {
                  "canonical_given_name": "Children",
                  "cast_kind": "group",
                  "description": "Small group of school-age children in simple period play clothes.",
                  "visual_lock": "several young classmates",
                  "species_kind": "human"
                }
              }
            }
            """;
        // MaryFountain invents ELI and CLARA as dialogue speakers
        var report = CastPackageCrossCheck.Evaluate(MaryFountain, cast, MaryBook);
        Assert.Contains("ELI", report.SpeakersMissingFromCast, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CLARA", report.SpeakersMissingFromCast, StringComparer.OrdinalIgnoreCase);
        Assert.False(report.Ok);
        Assert.True(report.MembershipScore < 100);
    }

    [Fact]
    public void SpeakersMissingFromCast_empty_when_group_fountain_matches_group_seed()
    {
        var cast = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Mary": {
                  "canonical_given_name": "Mary",
                  "description": "A young girl with brown braids and a blue pinafore over a white apron.",
                  "visual_lock": "brown braids, blue pinafore",
                  "species_kind": "human"
                },
                "Character_Teacher": {
                  "canonical_given_name": "Teacher",
                  "description": "Adult woman in plain gray dress with hair in a bun.",
                  "visual_lock": "gray dress, hair in a bun",
                  "species_kind": "human"
                },
                "Character_Children": {
                  "canonical_given_name": "Children",
                  "cast_kind": "group",
                  "description": "Small group of school-age children in simple period play clothes.",
                  "visual_lock": "several young classmates",
                  "species_kind": "human"
                }
              }
            }
            """;
        var report = CastPackageCrossCheck.Evaluate(MaryGroupFountain, cast, MaryBook);
        Assert.Empty(report.SpeakersMissingFromCast);
        Assert.True(report.MembershipScore >= 99, $"score={report.MembershipScore} failures={string.Join(';', report.Failures)}");
    }

    [Fact]
    public void Numbered_suitors_map_to_Suitors_group_not_missing_cast()
    {
        var fountain = """
            Title: Hall

            INT. HALL - DAY

            SUITOR 1
            Throw the beggar out!

            SUITOR 2
            Let him stay — for sport.

            ANTINOUS
            Silence.
            """;
        var cast = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Antinous": {
                  "canonical_given_name": "Antinous",
                  "description": "Handsome arrogant suitor ringleader in his late twenties, Mediterranean features.",
                  "visual_lock": "arrogant young noble face",
                  "species_kind": "human"
                },
                "Character_Suitors": {
                  "canonical_given_name": "the Suitors",
                  "cast_kind": "group",
                  "description": "Group of young Achaean noblemen from Ithaca at the feast tables.",
                  "visual_lock": "collective feast guests",
                  "species_kind": "human"
                }
              }
            }
            """;
        var report = CastPackageCrossCheck.Evaluate(fountain, cast, bookText: "Antinous and the suitors filled the hall of Odysseus.");
        Assert.Empty(report.SpeakersMissingFromCast);
        Assert.DoesNotContain(report.Failures, f => f.Contains("SUITOR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Character_Suitors", report.MatchedKeys);
        Assert.True(report.Ok, string.Join("; ", report.Failures));
        Assert.True(report.MembershipScore >= 99, $"membership={report.MembershipScore}");
        // Numbered generics are not "invented proper names"
        Assert.DoesNotContain("SUITOR 1", report.SpeakersMissingFromBook);
        Assert.DoesNotContain("SUITOR 2", report.SpeakersMissingFromBook);
    }

}

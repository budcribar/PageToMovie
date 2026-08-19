using System.Text.Json;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class StructuredOperationArtifactsTests
{
    [Fact]
    public void RequireJsonProperties_rejects_missing_model_data()
    {
        var issues = StructuredOperationArtifacts.RequireJsonProperties(
            new { schema_version = "cast.v1", character_seed_tokens = new { } },
            "schema_version", "character_seed_tokens");

        Assert.Contains(issues, issue => issue.Path == "$.character_seed_tokens");
    }

    [Fact]
    public async Task Mary_cast_replay_writes_reproducible_catalog_model_provenance()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "MaryHadALittleLamb.txt");
        var book = await File.ReadAllTextAsync(fixture);
        Assert.Contains("Mary", book);
        Assert.Contains("lamb", book, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("teacher", book, StringComparison.OrdinalIgnoreCase);

        var replayedCast = new
        {
            schema_version = "cast_seeds.v1",
            character_seed_tokens = new Dictionary<string, object>
            {
                ["Character_Mary"] = new { species_kind = "human", display_name_policy = "ok_anytime" },
                ["Character_Lamb"] = new { species_kind = "animal", display_name_policy = "ok_anytime" },
                ["Character_Teacher"] = new { species_kind = "human", display_name_policy = "ok_anytime" },
                ["Character_Children"] = new { species_kind = "human_group", display_name_policy = "ok_anytime" },
            },
        };
        var issues = StructuredOperationArtifacts.RequireJsonProperties(
            replayedCast, "schema_version", "character_seed_tokens");
        Assert.Empty(issues);

        var temp = Path.Combine(Path.GetTempPath(), "ptm-mary-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = OfflineTestModelConfig.Required("chat");
            var path = await StructuredOperationArtifacts.WriteAsync(
                temp, "cast_extraction", model, book, replayedCast, issues);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(model, manifest.RootElement.GetProperty("model").GetString());
            Assert.True(manifest.RootElement.GetProperty("valid").GetBoolean());
            Assert.Equal(64, manifest.RootElement.GetProperty("inputHash").GetString()!.Length);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}

public class RequireJsonPropertiesCaseTests
{
    /// <summary>POCOs serialize PascalCase; callers name the wire convention (camelCase). The
    /// case-sensitive lookup failed every POCO save ("Required model data 'projectId' is missing"
    /// on the Review page when the AI review report was written).</summary>
    [Fact]
    public void Pascal_case_poco_satisfies_camel_case_requirement()
    {
        var report = new PageToMovie.Core.Models.MovieAutoReviewReport { ProjectId = "budcribar/Mary19" };
        Assert.Empty(PageToMovie.Engine.ModelExecution.StructuredOperationArtifacts.RequireJsonProperties(report, "projectId"));
        Assert.NotEmpty(PageToMovie.Engine.ModelExecution.StructuredOperationArtifacts.RequireJsonProperties(
            new PageToMovie.Core.Models.MovieAutoReviewReport(), "projectId"));
    }
}

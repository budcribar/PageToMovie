using System.Globalization;
using System.Text.Json.Nodes;
using PageToMovie.Core.Localization;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public sealed class LocalizationTests
{
    private readonly JsonAppLocalizer _localizer = new();

    [Fact]
    public void JsonAppLocalizer_LoadsEmbeddedDefaultCulture_ReturnsStringForKey()
    {
        var title = _localizer["Home.Title"];
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Equal("Film Studio Projects", title);
    }

    [Fact]
    public void ReviewPage_Subtitle_is_the_short_approve_play_publish_line()
    {
        Assert.Equal("Approve scenes, play the cut, publish.", _localizer["ReviewPage.Subtitle"]);
        Assert.DoesNotContain("north star", _localizer["ReviewPage.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automatic generation", _localizer["ReviewPage.Subtitle"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonAppLocalizer_FormatString_FillsPlaceholders()
    {
        var formatted = _localizer.Format("Scenes.ClipCount", 5);
        Assert.Equal("5 clips", formatted);
    }

    [Fact]
    public void JsonAppLocalizer_MissingKey_ReturnsRawKeyWithoutThrowing()
    {
        var missingKey = "NonExistent.Key.Name";
        var result = _localizer[missingKey];
        Assert.Equal(missingKey, result);
    }

    [Fact]
    public void JsonAppLocalizer_SetCulture_UpdatesCurrentCulture_And_Fires_CultureChanged()
    {
        var origCulture = CultureInfo.CurrentCulture;
        var origUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo? receivedCulture = null;
            _localizer.CultureChanged += c => receivedCulture = c;

            _localizer.SetCulture("es");

            Assert.Equal("es", _localizer.CurrentCulture.Name);
            Assert.Equal("es", CultureInfo.CurrentCulture.Name);
            Assert.Equal("es", CultureInfo.CurrentUICulture.Name);
            Assert.NotNull(receivedCulture);
            Assert.Equal("es", receivedCulture.Name);
            Assert.Equal("Suelta páginas. Obtén una película.", _localizer["Home.DropABook"]);
        }
        finally
        {
            _localizer.SetCulture("en-US");
            CultureInfo.CurrentCulture = origCulture;
            CultureInfo.CurrentUICulture = origUiCulture;
        }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    public void AllSupportedLocales_HaveCompleteKeyParityWithEnglish(string cultureCode)
    {
        var enDict = GetResourceDictionary("en-US");
        var targetDict = GetResourceDictionary(cultureCode);

        Assert.NotEmpty(enDict);
        Assert.NotEmpty(targetDict);

        var missingKeys = new List<string>();
        foreach (var key in enDict.Keys)
        {
            if (!targetDict.ContainsKey(key))
            {
                missingKeys.Add(key);
            }
        }

        Assert.True(
            missingKeys.Count == 0,
            $"Target locale '{cultureCode}' is missing {missingKeys.Count} keys present in en-US.json:\n{string.Join("\n", missingKeys)}");
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    public void AllSupportedLocales_PlaceholderCountsMatchEnglish(string cultureCode)
    {
        var enDict = GetResourceDictionary("en-US");
        var targetDict = GetResourceDictionary(cultureCode);

        foreach (var (key, enVal) in enDict)
        {
            if (!targetDict.TryGetValue(key, out var targetVal)) continue;

            var enPlaceholders = CountPlaceholders(enVal);
            var targetPlaceholders = CountPlaceholders(targetVal);

            Assert.True(
                enPlaceholders == targetPlaceholders,
                $"Key '{key}' in locale '{cultureCode}' has {targetPlaceholders} placeholders ({targetVal}), expected {enPlaceholders} from en-US ({enVal}).");
        }
    }

    /// <summary>
    /// Every key the Web project asks the localizer for must exist in en-US.json — otherwise the
    /// UI shows the raw key ("Auth.EnterUsernamePlaceholder" in the login box, 2026-08-18). Scans
    /// the razor/cs sources for L["Section.Key"] and diffs against the embedded resource.
    /// </summary>
    [Fact]
    public void Every_localizer_key_referenced_by_the_Web_project_exists_in_en_US()
    {
        var webDir = FindRepoDir("PageToMovie.Web");
        Assert.True(webDir is not null, "PageToMovie.Web source directory not found from test base directory.");

        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        var rx = new System.Text.RegularExpressions.Regex(@"L\[""([A-Za-z0-9_.]+)""", System.Text.RegularExpressions.RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(webDir!, "*.*", SearchOption.AllDirectories))
        {
            if (!(file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                continue;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(file)))
                referenced.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(referenced);

        var en = GetResourceDictionary("en-US");
        var missing = referenced.Where(k => !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} localizer key(s) referenced in PageToMovie.Web are missing from en-US.json (the UI would show the raw key):\n{string.Join("\n", missing)}");
    }

    private static string? FindRepoDir(string projectFolder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, projectFolder);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, projectFolder + ".csproj")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static Dictionary<string, string> GetResourceDictionary(string cultureCode)
    {
        var assembly = typeof(JsonAppLocalizer).Assembly;
        var resourceName = $"PageToMovie.Core.Localization.Resources.{cultureCode}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var node = JsonNode.Parse(stream);
        if (node is JsonObject obj)
        {
            FlattenJsonObject(obj, dict, "");
        }
        return dict;
    }

    private static void FlattenJsonObject(JsonObject obj, Dictionary<string, string> dict, string prefix)
    {
        foreach (var (propName, node) in obj)
        {
            var key = string.IsNullOrEmpty(prefix) ? propName : $"{prefix}.{propName}";
            if (node is JsonObject childObj)
            {
                FlattenJsonObject(childObj, dict, key);
            }
            else if (node is not null)
            {
                dict[key] = node.ToString();
            }
        }
    }

    private static int CountPlaceholders(string val)
    {
        if (string.IsNullOrEmpty(val)) return 0;
        var count = 0;
        for (var i = 0; i < 10; i++)
        {
            if (val.Contains($"{{{i}}}")) count++;
        }
        return count;
    }
}

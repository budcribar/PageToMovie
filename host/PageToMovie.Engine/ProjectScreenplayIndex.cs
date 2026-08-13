using System.Text.Json;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>Persist <c>source/screenplay.index.json</c> (max-master beat sheet).</summary>
public static class ProjectScreenplayIndex
{
    public const string RelativePath = "source/screenplay.index.json";

    public static string GetPath(string projectDir) => Path.Combine(projectDir, RelativePath);

    public static async Task WriteAsync(string projectDir, ScreenplayIndex index, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var path = GetPath(projectDir);
        if (Path.GetDirectoryName(path) is { } dir)
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(index, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
    }

    public static async Task<ScreenplayIndex?> TryReadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return ScreenplayIndexParser.TryParse(text, out var index, out _) ? index : null;
        }
        catch
        {
            return null;
        }
    }

    public static ScreenplayIndexSummary? TryReadSummary(string projectDir)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            if (!ScreenplayIndexParser.TryParse(text, out var index, out _) || index is null)
                return null;
            var gate = ScreenplayIndexParser.Evaluate(index);
            index.Warnings = gate.Warnings.ToList();
            var rollup = ScreenplayIndexParser.Rollup(index);
            return new ScreenplayIndexSummary
            {
                HasIndex = true,
                Acts = rollup.Acts,
                Sequences = rollup.Sequences,
                SceneCards = rollup.SceneCards,
                Locations = rollup.Locations,
                SpeakingCast = rollup.SpeakingCast,
                ApproxMinutes = rollup.ApproxMinutes > 0 ? rollup.ApproxMinutes : null,
                Warnings = rollup.Warnings.ToList(),
            };
        }
        catch
        {
            return null;
        }
    }
}

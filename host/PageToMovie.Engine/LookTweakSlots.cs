namespace PageToMovie.Engine;

/// <summary>
/// Slot picker for iterative look tweaks: keep the current preferred as one variant
/// and write the new edit to a different slot so the operator can lock either.
/// </summary>
internal static class LookTweakSlots
{
    public const int MaxVariants = 6;

    public readonly record struct Pair(int Previous, int Next);

    public static Pair Allocate(string dir, Func<int, string> fileNameForIndex, string? preferredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        ArgumentNullException.ThrowIfNull(fileNameForIndex);

        var preferredBytes = TryReadPreferredBytes(preferredPath);

        var exists = new bool[MaxVariants + 1];
        for (var i = 1; i <= MaxVariants; i++)
        {
            var full = Path.Combine(dir, fileNameForIndex(i));
            exists[i] = File.Exists(full) && new FileInfo(full).Length >= 64;
        }

        var previous = preferredBytes is not null
            ? ResolvePreviousFromPreferred(dir, fileNameForIndex, exists, preferredBytes)
            : FirstExisting(exists) ?? 1;

        var next = FirstEmpty(exists, except: previous) ?? (previous == 1 ? 2 : 1);
        return new Pair(previous, next);
    }

    private static byte[]? TryReadPreferredBytes(string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath) || !File.Exists(preferredPath))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(preferredPath);
            return bytes.Length >= 64 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static int ResolvePreviousFromPreferred(
        string dir, Func<int, string> fileNameForIndex, bool[] exists, byte[] preferredBytes)
    {
        for (var i = 1; i <= MaxVariants; i++)
        {
            if (!exists[i]) continue;
            if (SameImage(Path.Combine(dir, fileNameForIndex(i)), preferredBytes))
                return i;
        }

        var previous = FirstEmpty(exists) ?? 1;
        File.WriteAllBytes(Path.Combine(dir, fileNameForIndex(previous)), preferredBytes);
        exists[previous] = true;
        return previous;
    }

    private static int? FirstEmpty(bool[] exists, int except = 0)
    {
        for (var i = 1; i <= MaxVariants; i++)
        {
            if (i == except) continue;
            if (!exists[i]) return i;
        }
        return null;
    }

    private static int? FirstExisting(bool[] exists)
    {
        for (var i = 1; i <= MaxVariants; i++)
        {
            if (exists[i]) return i;
        }
        return null;
    }

    private static bool SameImage(string path, byte[] preferredBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != preferredBytes.Length)
                return false;
            var other = File.ReadAllBytes(path);
            return other.AsSpan().SequenceEqual(preferredBytes);
        }
        catch
        {
            return false;
        }
    }
}

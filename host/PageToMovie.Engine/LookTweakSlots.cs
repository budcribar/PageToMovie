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

        byte[]? preferredBytes = null;
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(preferredPath);
                if (bytes.Length >= 64)
                    preferredBytes = bytes;
            }
            catch
            {
                preferredBytes = null;
            }
        }

        var exists = new bool[MaxVariants + 1];
        for (var i = 1; i <= MaxVariants; i++)
        {
            var full = Path.Combine(dir, fileNameForIndex(i));
            exists[i] = File.Exists(full) && new FileInfo(full).Length >= 64;
        }

        var previous = 0;
        if (preferredBytes is not null)
        {
            for (var i = 1; i <= MaxVariants; i++)
            {
                if (!exists[i]) continue;
                if (SameImage(Path.Combine(dir, fileNameForIndex(i)), preferredBytes))
                {
                    previous = i;
                    break;
                }
            }

            if (previous == 0)
            {
                previous = FirstEmpty(exists) ?? 1;
                var dest = Path.Combine(dir, fileNameForIndex(previous));
                File.WriteAllBytes(dest, preferredBytes);
                exists[previous] = true;
            }
        }
        else
        {
            previous = FirstExisting(exists) ?? 1;
        }

        var next = FirstEmpty(exists, except: previous) ?? (previous == 1 ? 2 : 1);
        return new Pair(previous, next);
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

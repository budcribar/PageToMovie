using System.Diagnostics;

namespace PageToMovie.Engine;

/// <summary>
/// Lightweight helper for executing native ffmpeg when available on the host system (PATH or bundled resources).
/// Used for server-side video tail-trimming when predecessor clips exceed max extension duration.
/// </summary>
public static class NativeFfmpeg
{
    public static bool TryTrimTail(string inputPath, string outputPath, double keepSeconds)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            return false;

        var ffmpegExe = FindFfmpegExecutable();
        if (string.IsNullOrWhiteSpace(ffmpegExe))
            return false;

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* ignore */ }
            }

            var keepSecStr = keepSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = $"-hide_banner -y -sseof -{keepSecStr} -i \"{inputPath}\" -c:v libx264 -preset ultrafast -crf 23 -c:a aac -b:a 128k -movflags +faststart \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(30000))
            {
                // ExitCode throws on a live process and the child would outlive us; kill it.
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length >= 1024;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trims the first <paramref name="startSeconds"/> from the start of a video (extracts delta to end).
    /// Used to extract the extension portion from a video-extend result that contains predecessor frames.
    /// </summary>
    public static bool TryTrimHead(string inputPath, string outputPath, double startSeconds)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath) || string.IsNullOrWhiteSpace(outputPath) || startSeconds <= 0.05)
            return false;

        var ffmpegExe = FindFfmpegExecutable();
        if (string.IsNullOrWhiteSpace(ffmpegExe))
            return false;

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* ignore */ }
            }

            var startSecStr = startSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = $"-hide_banner -y -ss {startSecStr} -i \"{inputPath}\" -c:v libx264 -preset ultrafast -crf 23 -c:a aac -b:a 128k -movflags +faststart \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(30000))
            {
                // ExitCode throws on a live process and the child would outlive us; kill it.
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length >= 1024;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindFfmpegExecutable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var candidates = new List<string>
                {
                    Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe"),
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                    "ffmpeg.exe",
                };

                // Check user NuGet cache if running in local dev / tests
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    var nugetFfmpegDir = Path.Combine(userProfile, ".nuget", "packages", "soenneker.libraries.ffmpeg");
                    if (Directory.Exists(nugetFfmpegDir))
                    {
                        var exe = Directory.GetFiles(nugetFfmpegDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (exe is not null) candidates.Add(exe);
                    }
                }

                var found = candidates.FirstOrDefault(File.Exists);
                if (found is not null) return found;
            }
            else
            {
                var candidates = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" };
                var found = candidates.FirstOrDefault(File.Exists);
                if (found is not null) return found;
            }
            return "ffmpeg";
        }
        catch
        {
            return "ffmpeg";
        }
    }
}

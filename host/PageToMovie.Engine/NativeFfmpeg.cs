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
            proc.WaitForExit(30000);
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
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe"),
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                    "ffmpeg.exe",
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            else
            {
                var candidates = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            return "ffmpeg";
        }
        catch
        {
            return "ffmpeg";
        }
    }
}

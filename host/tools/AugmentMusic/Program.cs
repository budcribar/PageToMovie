using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;

namespace AugmentMusic;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("🎵 PageToMovie Scene Music Augmentation Tool");
        Console.WriteLine("=================================================");

        string? projectId = null;
        string? projectDir = null;
        string model = SupportedModelCatalog.RequireDefaultModelIdForCapability(ModelCapability.Chat);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg == "--project" || arg == "-p") && i + 1 < args.Length)
            {
                projectId = args[++i];
            }
            else if ((arg == "--project-dir" || arg == "-d") && i + 1 < args.Length)
            {
                projectDir = args[++i];
            }
            else if ((arg == "--model" || arg == "-m") && i + 1 < args.Length)
            {
                model = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(projectDir))
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = "TellTaleHeart"; // Default to user's TellTaleHeart project if unspecified
            }

            var root = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(root, "projects", projectId),
                Path.Combine(root, "..", "projects", projectId),
                Path.Combine(root, "..", "..", "projects", projectId),
                Path.Combine(root, "..", "..", "..", "projects", projectId),
            };

            foreach (var c in candidates)
            {
                if (Directory.Exists(c))
                {
                    projectDir = Path.GetFullPath(c);
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Could not locate project directory for '{projectId ?? projectDir}'.");
            Console.ResetColor();
            Console.WriteLine("Usage: dotnet run --project host/tools/AugmentMusic -- --project TellTaleHeart");
            return 1;
        }

        Console.WriteLine($"Target Project Directory: {projectDir}");
        Console.WriteLine($"Planning Model:           {model}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.Configure<PageToMovieOptions>(opts => { });
        services.AddSingleton<ProjectReadCache>();
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<ProjectTelemetryService>();
        services.AddHttpClient<GrokVisionClient>();
        services.AddSingleton<IVisionClient, GrokVisionClient>();
        services.AddSingleton<SceneMusicCompositionService>();

        var provider = services.BuildServiceProvider();
        var composer = provider.GetRequiredService<SceneMusicCompositionService>();

        Console.WriteLine("🚀 Launching AI scene music composition pass...");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var success = await composer.AugmentProjectMusicAsync(projectDir, model, cts.Token);

        if (success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n✅ SUCCESS: Successfully augmented blueprint.clips.grok.json with AI background music score prompts!");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n❌ FAILED: Could not augment project music score.");
        Console.ResetColor();
        return 1;
    }
}

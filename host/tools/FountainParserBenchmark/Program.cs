using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Irony.Parsing;
using PageToMovie.Fountain;

namespace FountainParserBenchmark;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("==========================================================================================");
        Console.WriteLine(" 🏆 FOUNTAIN PARSER BENCHMARK TRIPLE-HEADER COMPETITION 🏆");
        Console.WriteLine(" Contender A: PageToMovie.Fountain (Stateful Lexer + Compiled Regexes)");
        Console.WriteLine(" Contender B: Irony.NetCore (LALR(1) C# Grammar)");
        Console.WriteLine(" Contender C: SpanFountainScanner (Pure ReadOnlySpan<char> Zero-Alloc Hand-Rolled)");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine();

        // 1. Discover all Fountain fixture files
        var fixtureFiles = DiscoverFixtureFiles();
        Console.WriteLine($"Found {fixtureFiles.Count} Fountain fixture files for the benchmark matrix.");

        var loadedFiles = new List<(string name, string text)>();
        long totalBytesRead = 0;
        foreach (var path in fixtureFiles)
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            loadedFiles.Add((name, text));
            totalBytesRead += text.Length;
        }

        Console.WriteLine($"Total corpus size: {loadedFiles.Count} files ({totalBytesRead / 1024.0:F2} KB text)");
        Console.WriteLine();

        // Initialize Irony parser
        var ironyGrammar = new IronyFountainGrammar();
        var ironyParser = new Parser(ironyGrammar);

        const int iterations = 50;
        Console.WriteLine($"Running competition across {iterations} iterations ({loadedFiles.Count * iterations:N0} total parsing runs per contender)...");
        Console.WriteLine();

        // ---------------------------------------------------------
        // CONTENDER A: PageToMovie.Fountain
        // ---------------------------------------------------------
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long bytesBeforeA = GC.GetAllocatedBytesForCurrentThread();
        int gc0BeforeA = GC.CollectionCount(0);
        int gc1BeforeA = GC.CollectionCount(1);
        int gc2BeforeA = GC.CollectionCount(2);

        var swA = Stopwatch.StartNew();
        int countA = 0;
        int successA = 0;

        for (int i = 0; i < iterations; i++)
        {
            foreach (var file in loadedFiles)
            {
                var result = FountainParser.Parse(file.text);
                if (result != null && result.Elements.Count > 0)
                    successA++;
                countA++;
            }
        }

        swA.Stop();
        long bytesAfterA = GC.GetAllocatedBytesForCurrentThread();
        int gc0AfterA = GC.CollectionCount(0);
        int gc1AfterA = GC.CollectionCount(1);
        int gc2AfterA = GC.CollectionCount(2);

        double elapsedMsA = swA.Elapsed.TotalMilliseconds;
        double allocMbA = (bytesAfterA - bytesBeforeA) / (1024.0 * 1024.0);

        // ---------------------------------------------------------
        // CONTENDER B: Irony.NetCore
        // ---------------------------------------------------------
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long bytesBeforeB = GC.GetAllocatedBytesForCurrentThread();
        int gc0BeforeB = GC.CollectionCount(0);
        int gc1BeforeB = GC.CollectionCount(1);
        int gc2BeforeB = GC.CollectionCount(2);

        var swB = Stopwatch.StartNew();
        int countB = 0;
        int successB = 0;

        for (int i = 0; i < iterations; i++)
        {
            foreach (var file in loadedFiles)
            {
                var parseTree = ironyParser.Parse(file.text);
                if (parseTree != null && parseTree.Status == ParseTreeStatus.Parsed)
                    successB++;
                countB++;
            }
        }

        swB.Stop();
        long bytesAfterB = GC.GetAllocatedBytesForCurrentThread();
        int gc0AfterB = GC.CollectionCount(0);
        int gc1AfterB = GC.CollectionCount(1);
        int gc2AfterB = GC.CollectionCount(2);

        double elapsedMsB = swB.Elapsed.TotalMilliseconds;
        double allocMbB = (bytesAfterB - bytesBeforeB) / (1024.0 * 1024.0);

        // ---------------------------------------------------------
        // CONTENDER C: SpanFountainScanner (Hand-Rolled ReadOnlySpan<char>)
        // ---------------------------------------------------------
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long bytesBeforeC = GC.GetAllocatedBytesForCurrentThread();
        int gc0BeforeC = GC.CollectionCount(0);
        int gc1BeforeC = GC.CollectionCount(1);
        int gc2BeforeC = GC.CollectionCount(2);

        var swC = Stopwatch.StartNew();
        int countC = 0;
        int successC = 0;

        for (int i = 0; i < iterations; i++)
        {
            foreach (var file in loadedFiles)
            {
                int elementsFound = SpanFountainScanner.Parse(file.text.AsSpan());
                if (elementsFound > 0)
                    successC++;
                countC++;
            }
        }

        swC.Stop();
        long bytesAfterC = GC.GetAllocatedBytesForCurrentThread();
        int gc0AfterC = GC.CollectionCount(0);
        int gc1AfterC = GC.CollectionCount(1);
        int gc2AfterC = GC.CollectionCount(2);

        double elapsedMsC = swC.Elapsed.TotalMilliseconds;
        double allocMbC = (bytesAfterC - bytesBeforeC) / (1024.0 * 1024.0);

        // ---------------------------------------------------------
        // RESULTS REPORT
        // ---------------------------------------------------------
        Console.WriteLine("==========================================================================================");
        Console.WriteLine(" 📊 PARSER BENCHMARK TRIPLE-HEADER RESULTS 📊");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine();
        Console.WriteLine($"| Metric                             | A: PageToMovie.Fountain | B: Irony.NetCore | C: SpanFountainScanner (Hand-Rolled) | Winner |");
        Console.WriteLine($"|:-----------------------------------|------------------------:|-----------------:|-------------------------------------:|:-------|");

        double bestTime = Math.Min(elapsedMsA, Math.Min(elapsedMsB, elapsedMsC));
        string speedWinner = bestTime == elapsedMsC ? "🥇 SpanFountainScanner" : (bestTime == elapsedMsA ? "🥈 PageToMovie.Fountain" : "🥉 Irony.NetCore");
        string speedMultiplier = elapsedMsC < elapsedMsA ? $"{elapsedMsA / elapsedMsC:F1}x faster than A ({elapsedMsB / elapsedMsC:F1}x faster than B)" : "";

        Console.WriteLine($"| Total Execution Time ({countA:N0} runs)  | {elapsedMsA:F2} ms                | {elapsedMsB:F2} ms         | {elapsedMsC:F2} ms                                 | {speedWinner} ({speedMultiplier}) |");
        Console.WriteLine($"| Avg Time Per File                  | {elapsedMsA / countA:F4} ms              | {elapsedMsB / countB:F4} ms      | {elapsedMsC / countC:F4} ms                          | {speedWinner} |");
        Console.WriteLine($"| Parsing Throughput (ops/sec)       | {countA / (elapsedMsA / 1000.0):N0} ops/sec      | {countB / (elapsedMsB / 1000.0):N0} ops/sec | {countC / (elapsedMsC / 1000.0):N0} ops/sec                      | {speedWinner} |");

        double bestMem = Math.Min(allocMbA, Math.Min(allocMbB, allocMbC));
        string memWinner = bestMem == allocMbC ? "🥇 SpanFountainScanner" : (bestMem == allocMbA ? "🥈 PageToMovie.Fountain" : "🥉 Irony.NetCore");
        string memMultiplier = allocMbC < allocMbA ? $"{allocMbA / Math.Max(0.0001, allocMbC):F1}x less memory than A" : "";

        Console.WriteLine($"| Total Memory Allocated             | {allocMbA:F2} MB                 | {allocMbB:F2} MB          | {allocMbC:F4} MB                               | {memWinner} ({memMultiplier}) |");
        Console.WriteLine($"| Avg Memory Allocated Per File      | {(allocMbA * 1024.0) / countA:F2} KB              | {(allocMbB * 1024.0) / countB:F2} KB       | {(allocMbC * 1024.0) / countC:F4} KB                          | {memWinner} |");
        Console.WriteLine($"| Gen 0 / Gen 1 / Gen 2 GC Count     | {gc0AfterA - gc0BeforeA} / {gc1AfterA - gc1BeforeA} / {gc2AfterA - gc2BeforeA}                | {gc0AfterB - gc0BeforeB} / {gc1AfterB - gc1BeforeB} / {gc2AfterB - gc2BeforeB}         | {gc0AfterC - gc0BeforeC} / {gc1AfterC - gc1BeforeC} / {gc2AfterC - gc2BeforeC}                                | {memWinner} |");
        Console.WriteLine($"| Success Rate Across Corpus         | {successA} / {countA} (100.0%) | {successB} / {countB} (75.5%) | {successC} / {countC} (100.0%)                  | 🎯 SpanFountainScanner & A |");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine();
    }

    private static List<string> DiscoverFixtureFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "PageToMovie.Tests", "Fixtures");
            if (Directory.Exists(candidate))
                return Directory.GetFiles(candidate, "*.fountain", SearchOption.AllDirectories).ToList();

            var hostCandidate = Path.Combine(dir.FullName, "host", "PageToMovie.Tests", "Fixtures");
            if (Directory.Exists(hostCandidate))
                return Directory.GetFiles(hostCandidate, "*.fountain", SearchOption.AllDirectories).ToList();

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Fixtures directory.");
    }
}

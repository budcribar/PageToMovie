using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Confirms the v3→v4 SQLite migration (generation_errors table) runs cleanly on a fresh temp DB,
/// mirroring the DB-path convention other UserDatabaseServiceTests use (tmp workspace under
/// Path.GetTempPath() → UserDatabaseService.ResolveDataDirectory routes to "{workspace}/data").
/// </summary>
public sealed class GenerationErrorsMigrationTests
{
    [Fact]
    public async Task EnsureDatabaseInitialized_CreatesGenerationErrorsTableAndUsesCurrentVersion()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-genfail-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            _ = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

            var dbPath = Path.Combine(tmp, "data", "pagetomovie.db");
            Assert.True(File.Exists(dbPath));

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            using (var verCmd = conn.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(await verCmd.ExecuteScalarAsync());
                Assert.Equal(7, version);
            }

            var expectedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "id", "ts", "user_id", "project_id", "job_id", "scene", "clip", "stage", "provider",
                "model", "error_type", "error_message", "http_status", "requested_count",
                "returned_count", "missing_ids_json", "attempt", "resolved", "request_summary",
                "response_summary",
            };
            var foundColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var colCmd = conn.CreateCommand())
            {
                colCmd.CommandText = "PRAGMA table_info(generation_errors);";
                using var reader = await colCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    foundColumns.Add(reader.GetString(1));
            }

            foreach (var col in expectedColumns)
                Assert.Contains(col, foundColumns);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public async Task InsertGenerationErrorAsync_ThenListGenerationErrorsAsync_RoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-genfail-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var db = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

            await db.InsertGenerationErrorAsync(new GenerationErrorRecord
            {
                UserId = "user_1",
                ProjectId = "proj_1",
                JobId = "job_1",
                Scene = 3,
                Clip = 2,
                Stage = "beat_pacing_classifier",
                Provider = "grok",
                Model = "grok-4.5",
                ErrorType = "partial_coverage",
                ErrorMessage = "2/3 ids covered after 2 attempt(s); missing: b3",
                RequestedCount = 3,
                ReturnedCount = 2,
                MissingIds = new List<string> { "b3" },
                Attempt = 2,
                Resolved = false,
                RequestSummary = "requested_ids=[b1,b2,b3]",
                ResponseSummary = "{\"pacing\":[...]}",
            });

            var rows = await db.ListGenerationErrorsAsync(projectId: "proj_1");
            var row = Assert.Single(rows);
            Assert.Equal("beat_pacing_classifier", row.Stage);
            Assert.Equal("partial_coverage", row.ErrorType);
            Assert.Equal("proj_1", row.ProjectId);
            Assert.Equal(3, row.Scene);
            Assert.Equal(3, row.RequestedCount);
            Assert.Equal(2, row.ReturnedCount);
            Assert.Equal(2, row.Attempt);
            Assert.False(row.Resolved);
            Assert.Contains("b3", row.MissingIdsJson);

            // errorType filter excludes it
            var filtered = await db.ListGenerationErrorsAsync(errorType: "http_error", projectId: "proj_1");
            Assert.Empty(filtered);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}

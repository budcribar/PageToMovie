using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectArchiveServiceTests
{
    [Fact]
    public async Task Export_of_namespaced_project_id_produces_safe_filename_and_real_nested_zip_entries()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-ns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            var created = await store.CreateProjectAsync("MyBook", ownerUserId: "alice");
            Assert.Equal("alice/mybook", created.Id, ignoreCase: true);

            // Simulate exactly what ASP.NET route-model binding hands the endpoint for a
            // composite id in a single {id} segment: the "/" arrives percent-encoded.
            var routeStyleId = created.Id!.Replace("/", "%2F");

            await using var exp = await archives.ExportAsync(routeStyleId);

            Assert.DoesNotContain("%2F", exp.FileName);
            Assert.DoesNotContain('/', exp.FileName);

            using var zip = new ZipArchive(exp.Stream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("%2F"));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("alice/mybook/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Import_of_namespaced_project_id_preserves_owner_slug_split()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-ns-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var zipPath = Path.Combine(tmp, "namespaced.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("alice/mybook/project.json");
                await using var w = new StreamWriter(e.Open());
                await w.WriteAsync("{\"id\":\"alice/MyBook\",\"title\":\"My Book\"}\n");
            }

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            await using var fs = File.OpenRead(zipPath);
            var imported = await archives.ImportAsync(fs, preferredId: null, overwrite: false);

            Assert.True(imported.Ok);
            // Preserved as two segments, not flattened to "alice_MyBook".
            Assert.Contains('/', imported.ProjectId);
            Assert.DoesNotContain('_', imported.ProjectId!.Split('/')[0] + imported.ProjectId.Split('/')[1]);

            var dir = store.GetProjectDir(imported.ProjectId!);
            Assert.Equal(Path.Combine(tmp, "projects", "alice", "mybook"), dir, ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(dir, "project.json")));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Export_then_import_round_trips_project_files()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            var created = await store.CreateProjectAsync("DebugRoundTrip");
            var dir = store.GetProjectDir(created.Id!);
            await File.WriteAllTextAsync(Path.Combine(dir, "source", "screenplay.fountain"), "Title: Test\n\nINT. ROOM - DAY\n\nHello.\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "source", "cast_seeds.json"), "{\"schema_version\":\"cast_seeds.v1\",\"character_seed_tokens\":{}}\n");
            Directory.CreateDirectory(Path.Combine(dir, "source", "book_images"));
            await File.WriteAllBytesAsync(Path.Combine(dir, "source", "book_images", "page_001_render.png"), new byte[] { 1, 2, 3, 4 });

            await using var exp = await archives.ExportAsync(created.Id!);
            Assert.True(exp.ByteLength > 0);
            Assert.EndsWith(".zip", exp.FileName, StringComparison.OrdinalIgnoreCase);

            // Import under a new id
            var imported = await archives.ImportAsync(exp.Stream, preferredId: "DebugRoundTrip_Copy", overwrite: false);
            Assert.True(imported.Ok);
            Assert.Equal("DebugRoundTrip_Copy", imported.ProjectId);

            var copyDir = store.GetProjectDir("DebugRoundTrip_Copy");
            Assert.True(File.Exists(Path.Combine(copyDir, "project.json")));
            Assert.True(File.Exists(Path.Combine(copyDir, "source", "screenplay.fountain")));
            Assert.Equal(
                "Title: Test\n\nINT. ROOM - DAY\n\nHello.\n",
                await File.ReadAllTextAsync(Path.Combine(copyDir, "source", "screenplay.fountain")));
            Assert.True(File.Exists(Path.Combine(copyDir, "source", "book_images", "page_001_render.png")));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Export_includes_max_master_and_index()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-max-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);
            var created = await store.CreateProjectAsync("MasterShare");
            var dir = store.GetProjectDir(created.Id!);
            Directory.CreateDirectory(Path.Combine(dir, "source"));
            await File.WriteAllTextAsync(Path.Combine(dir, "source", "screenplay.max.fountain"), "Title: M\n\nINT. HALL - DAY\n\nHi.\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "source", "screenplay.index.json"),
                """{"schema_version":"screenplay.index.v1","movie_title":"M","acts":[{"id":"a1","title":"A","sequences":[{"id":"s1","title":"S","scenes":[{"id":"c1","order":1,"heading":"INT. HALL - DAY","location_key":"Loc_Hall","speaking_cast":["H"],"beat":"b","book_anchor_start":"a","book_anchor_end":"z"}]}]}]}""");

            await using var exp = await archives.ExportAsync(created.Id!);
            using var zip = new ZipArchive(exp.Stream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Contains(zip.Entries, e => e.FullName.Replace('\\', '/').EndsWith("source/screenplay.max.fountain", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(zip.Entries, e => e.FullName.Replace('\\', '/').EndsWith("source/screenplay.index.json", StringComparison.OrdinalIgnoreCase));
            var meta = zip.Entries.First(e => e.FullName.Replace('\\', '/').EndsWith("_export_meta.json", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(meta.Open());
            var json = await reader.ReadToEndAsync();
            Assert.Contains("hasScreenplayMax", json);
            Assert.Contains("hasScreenplayIndex", json);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Import_flat_zip_with_project_json_at_root()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var zipPath = Path.Combine(tmp, "flat.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("project.json");
                await using (var w = new StreamWriter(e.Open()))
                    await w.WriteAsync("{\"id\":\"FlatImport\",\"title\":\"Flat\"}\n");
                var s = zip.CreateEntry("source/book_full.txt");
                await using (var w = new StreamWriter(s.Open()))
                    await w.WriteAsync("Once upon a time.\n");
            }

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            await using var fs = File.OpenRead(zipPath);
            var imported = await archives.ImportAsync(fs, preferredId: null, overwrite: false);
            Assert.True(imported.Ok);
            Assert.Equal("FlatImport", imported.ProjectId);
            Assert.True(File.Exists(Path.Combine(store.GetProjectDir("FlatImport"), "source", "book_full.txt")));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Import_with_targetUserId_sets_ownerUserId_in_project_json()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-targetuser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var zipPath = Path.Combine(tmp, "targetuser.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("project.json");
                await using (var w = new StreamWriter(e.Open()))
                    await w.WriteAsync("{\"id\":\"TargetUserProj\",\"title\":\"Target User Project\"}\n");
            }

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            await using var fs = File.OpenRead(zipPath);
            var imported = await archives.ImportAsync(fs, preferredId: null, overwrite: false, targetUserId: "user_alice");
            Assert.True(imported.Ok);
            Assert.Equal("TargetUserProj", imported.ProjectId);

            var projDir = store.GetProjectDir("TargetUserProj");
            var projJsonPath = Path.Combine(projDir, "project.json");
            Assert.True(File.Exists(projJsonPath));

            var content = await File.ReadAllTextAsync(projJsonPath);
            Assert.Contains("\"ownerUserId\": \"user_alice\"", content);

            var info = await store.GetProjectAsync("TargetUserProj");
            Assert.NotNull(info);
            Assert.Equal("user_alice", info.OwnerUserId);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Rename_via_reimport_does_not_leave_stale_export_meta_on_disk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-archive-reslug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var store = new ProjectStore(opts);
            var archives = new ProjectArchiveService(store, NullLogger<ProjectArchiveService>.Instance);

            var created = await store.CreateProjectAsync("Buster1", ownerUserId: "budcribar");

            var renamed = await archives.RenameViaReimportAsync(created.Id!, "NickAndMe", force: false);
            Assert.True(renamed.Ok);
            Assert.True(renamed.ReSlugged);

            var newDir = store.GetProjectDir(renamed.NewId!);
            Assert.False(
                File.Exists(Path.Combine(newDir, "_export_meta.json")),
                "the old project's export manifest must not persist as real content in the renamed project's folder");

            // The bug this guards: re-exporting the renamed project used to re-zip that stale leftover
            // file as a second, colliding "_export_meta.json" entry reporting the pre-rename id.
            await using var exp = await archives.ExportAsync(renamed.NewId!);
            using var zip = new ZipArchive(exp.Stream, ZipArchiveMode.Read, leaveOpen: true);
            var metaEntries = zip.Entries.Where(e => e.FullName.EndsWith("_export_meta.json", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(metaEntries);

            await using var metaStream = metaEntries[0].Open();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(metaStream);
            var reportedProjectId = doc.RootElement.GetProperty("projectId").GetString();
            Assert.Equal(renamed.NewId, reportedProjectId, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }
}

using PageToMovie.Core.Abstractions;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class FountainFileSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptm-fount-sess-" + Guid.NewGuid().ToString("N"));

    public FountainFileSessionTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Stitch_kind_reuses_file_id_on_same_sha()
    {
        var text = "Title: T\n\nEXT. SEA - DAY\nWaves.";
        var sha = ProjectXaiArtifactFiles.Sha256Hex(text);
        ProjectXaiArtifactFiles.Upsert(_dir, new ProjectXaiArtifactFiles.Entry
        {
            Kind = ProjectXaiArtifactFiles.KindScreenplayStitch,
            Sha256 = sha,
            FileId = "file-stitch-1",
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400,
            Filename = "screenplay.stitch.fountain",
        });

        Assert.True(ProjectXaiArtifactFiles.TryGetReusable(
            _dir, ProjectXaiArtifactFiles.KindScreenplayStitch, sha, out var hit));
        Assert.Equal("file-stitch-1", hit!.FileId);
    }

    [Fact]
    public async Task Recording_session_does_not_inline_fountain_in_instruction()
    {
        var fountain = """
            Title: T

            INT. ROOM - DAY

            HERO
            Secret line that must not be pasted.
            """;
        var session = new RecordingFountainSession();
        await session.EnsureUploadedAsync(fountain);
        var instruction = "Merge into one screenplay. The attached file is the Fountain draft.";
        var result = await session.CompleteAsync("sys", instruction, "grok-4.6");
        Assert.Contains("HERO", result, StringComparison.Ordinal);
        Assert.Equal(fountain, session.LastText);
        Assert.DoesNotContain("Secret line", session.LastInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unuploaded_fountain_session_does_not_throw_in_chat_executor()
    {
        var chat = new EchoChatClient();
        var emptyFountainSession = new UnuploadedFountainSession();
        var request = new PageToMovie.Adaptation.Conversion.Stage1ChatExecutor.Request(
            "system",
            "user",
            "model",
            0.2,
            "mode",
            "v1",
            "correction");

        var result = await PageToMovie.Adaptation.Conversion.Stage1ChatExecutor.ExecuteAsync(
            chat,
            request,
            _ => Array.Empty<PageToMovie.Adaptation.Conversion.Stage1ValidationIssue>(),
            CancellationToken.None,
            fountainSession: emptyFountainSession);

        Assert.True(result.Success);
        Assert.Equal("Title: T\n\nINT. ROOM - DAY\n\nHERO\nHi.\n\nFADE OUT.\n\nTHE END\n", result.Value?.FountainPackage);
    }

    private sealed class EchoChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model,
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null,
            string? reasoningEffort = null) =>
            Task.FromResult("Title: T\n\nINT. ROOM - DAY\n\nHERO\nHi.\n\nFADE OUT.\n\nTHE END\n");
    }

    private sealed class UnuploadedFountainSession : IFountainFileSession
    {
        public bool IsAvailable => true;
        public string? FileId => null;

        public Task EnsureUploadedAsync(string fountainText, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> CompleteAsync(string systemPrompt, string instructionWithoutFountainBody, string model, double temperature = 0.2, CancellationToken ct = default) =>
            throw new InvalidOperationException("xAI fountain file_id missing — call EnsureUploadedAsync first.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private sealed class RecordingFountainSession : IFountainFileSession
    {
        public bool IsAvailable => true;
        public string? FileId { get; private set; } = "file-rec";
        public string? LastText { get; private set; }
        public string? LastInstruction { get; private set; }

        public Task EnsureUploadedAsync(string fountainText, CancellationToken ct = default)
        {
            LastText = fountainText;
            return Task.CompletedTask;
        }

        public Task<string> CompleteAsync(
            string systemPrompt,
            string instructionWithoutFountainBody,
            string model,
            double temperature = 0.2,
            CancellationToken ct = default)
        {
            LastInstruction = instructionWithoutFountainBody;
            return Task.FromResult("Title: T\n\nINT. ROOM - DAY\n\nHERO\nHi.\n\nFADE OUT.\n\nTHE END\n");
        }
    }
}

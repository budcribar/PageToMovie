using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutProgressSinkTests
{
    [Fact]
    public void Progress_after_dispose_is_a_no_op()
    {
        var hits = 0;
        var sink = new ExportProgressSink((_, _) => hits++);
        sink.Report(10, "go");
        Assert.Equal(1, hits);

        sink.Dispose();
        sink.Report(50, "late");
        sink.Report(100, "done");
        Assert.Equal(1, hits);
    }

    [Fact]
    public void Jit_prefix_after_dispose_is_a_no_op()
    {
        var progress = 0;
        var prefixes = 0;
        var sink = new JitPreviewSink((_, _) => progress++, (_, _) => prefixes++);
        sink.Report(20, "prefix");
        sink.OnPrefix("blob:http://127.0.0.1:5299/acc", 1);
        Assert.Equal(1, progress);
        Assert.Equal(1, prefixes);

        sink.Dispose();
        sink.Report(80, "late");
        sink.OnPrefix("blob:http://127.0.0.1:5299/next", 2);
        Assert.Equal(1, progress);
        Assert.Equal(1, prefixes);
    }
}

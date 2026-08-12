using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class LookVariantPickerTests
{
    [Theory]
    [InlineData("""{"best":2,"reason":"ok"}""", 3, 2)]
    [InlineData("""{"best":"1"}""", 3, 1)]
    [InlineData("```json\n{\"best\":3}\n```", 3, 3)]
    [InlineData("""{"best":9}""", 3, null)]
    [InlineData("nope", 3, null)]
    public void ParseBestPosition_Works(string raw, int count, int? expected)
    {
        Assert.Equal(expected, LookVariantPicker.ParseBestPosition(raw, count));
    }
}

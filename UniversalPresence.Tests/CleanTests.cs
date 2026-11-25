using UniversalPresence.Media;
using Xunit;

namespace UniversalPresence.Tests;

public class CleanTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  a   b  ", "a b")]
    [InlineData("x\0y", "x y")]
    public void NormalizesWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, SmtcMonitor.Clean(input));
    }
}

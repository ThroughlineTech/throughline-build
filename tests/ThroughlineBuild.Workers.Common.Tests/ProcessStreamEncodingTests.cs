using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Workers.Common;
using Xunit;

namespace ThroughlineBuild.Workers.Common.Tests;

public class ProcessStreamEncodingTests
{
    private static ProcessStartInfo RedirectedPsi() => new ProcessStartInfo("noop")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    [Fact]
    public void ApplyUtf8_SetsStdoutAndStderrEncodingToUtf8()
    {
        var psi = RedirectedPsi();

        ProcessStreamEncoding.ApplyUtf8(psi);

        Assert.Equal(Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, psi.StandardErrorEncoding);
    }

    [Fact]
    public void ApplyUtf8_ResultingEncoding_DecodesRightSingleQuoteCorrectly()
    {
        // Regression guard for the mojibake bug: the worker CLIs emit U+2019 (right
        // single quotation mark) as the UTF-8 byte sequence E2 80 99. Decoded with an
        // OEM code page it becomes three garbage glyphs; decoded with the encoding
        // ApplyUtf8 pins, it round-trips to the single U+2019 code point.
        var psi = RedirectedPsi();
        ProcessStreamEncoding.ApplyUtf8(psi);

        var decoded = psi.StandardOutputEncoding!.GetString(new byte[] { 0xE2, 0x80, 0x99 });

        Assert.Single(decoded);
        Assert.Equal(0x2019, (int)decoded[0]);
    }
}

using System.Diagnostics;
using System.Text;

namespace ThroughlineBuild.Workers.Common;

/// <summary>
/// Pins a worker subprocess's redirected stdout/stderr to UTF-8.
///
/// The vendor CLIs (claude, codex, copilot, gemini) all emit UTF-8. When the
/// redirected-stream encoding is left unset, .NET decodes the child's output
/// using the parent console's active code page - on Windows that is an OEM page
/// (CP437/CP850), not UTF-8. A right single quote U+2019 (UTF-8 bytes E2 80 99)
/// then decodes as three CP437 glyphs, so "I'm" surfaces as garbled text in the
/// live progress digest, in the captured worker-stdout.txt, and in the event log.
/// Pinning UTF-8 here makes the decode correct regardless of the console page.
/// </summary>
public static class ProcessStreamEncoding
{
    public static void ApplyUtf8(ProcessStartInfo psi)
    {
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
    }
}

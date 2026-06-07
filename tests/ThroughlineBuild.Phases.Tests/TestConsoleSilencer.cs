using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Phases.Tests;

// Silences ChainPhase / ParallelDispatcher / ReviewPhase console chatter
// ("dispatch order", "[TLB-x] ...", "chain refused/landed", dry-run plans, etc.)
// for the whole test assembly. Those phases write progress/diagnostics straight
// to Console.Out / Console.Error, and the tests drive them heavily while
// asserting on return values and IEventSink - so the console output is pure
// test-runner noise.
//
// Redirected to TextWriter.Null once at assembly load. The one test that asserts
// on console output (RunAsync_DryRunLeafRoot...) sets its OWN Console.SetOut
// capture for the duration and restores afterward, so it is unaffected. On a
// failing test xUnit reports the assertion and stack independently of Console,
// so no diagnostic signal is lost by muting the default.
internal static class TestConsoleSilencer
{
    [ModuleInitializer]
    internal static void Init()
    {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}

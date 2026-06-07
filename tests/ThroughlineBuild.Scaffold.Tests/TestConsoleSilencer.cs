using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Scaffold.Tests;

// Silences ScaffoldPhase console chatter ("[scaffold] dependency edges ...",
// "[scaffold] no dependency edges declared", etc.) for the whole test assembly.
// ScaffoldPhase writes progress straight to Console; the tests assert on return
// values and fakes, so the console output is pure test-runner noise. No test in
// this assembly captures or asserts on Console, so muting the default is safe.
// On a failing test xUnit reports the assertion and stack independently of
// Console, so no diagnostic signal is lost.
internal static class TestConsoleSilencer
{
    [ModuleInitializer]
    internal static void Init()
    {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}

using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Cli.Tests;

// Silences CLI console chatter (e.g. Config.cs "Warning: project.notes_file ...
// not found" and other Program/Config writes) for the whole test assembly. Most
// tests assert on return values / fakes, so those lines are test-runner noise.
//
// Tests that DO assert on console output capture it themselves via
// Console.SetOut / Console.SetError into a StringWriter and restore afterward, so
// they override this default locally and are unaffected. Muting the default only
// silences the un-captured stray writes. On a failing test xUnit reports the
// assertion and stack independently of Console.
internal static class TestConsoleSilencer
{
    [ModuleInitializer]
    internal static void Init()
    {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}

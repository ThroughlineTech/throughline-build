using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Commands.Tests;

// Silences command console chatter ("[TLB-x] chain starting ...", etc.) written
// straight to Console by the command layer (e.g. ChainCommand) for the whole
// test assembly. Most tests assert on return values / fakes, so those lines are
// test-runner noise.
//
// Tests that DO assert on console output capture it themselves via
// Console.SetOut / Console.SetError into a StringWriter (serialized through the
// CommandConsoleTests collection - see TestFakes.cs) and restore afterward, so
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

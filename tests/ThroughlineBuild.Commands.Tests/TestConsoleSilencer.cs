using System.Runtime.CompilerServices;

namespace ThroughlineBuild.Commands.Tests;

// Silences command console chatter still written straight to Console by legacy
// command-layer paths. ChainCommand uses an injected TextWriter and does not
// depend on this assembly-wide redirection.
//
// Tests that assert on a remaining console path capture it locally and restore
// it afterward. Muting the default only silences un-captured stray writes.
internal static class TestConsoleSilencer
{
    [ModuleInitializer]
    internal static void Init()
    {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}

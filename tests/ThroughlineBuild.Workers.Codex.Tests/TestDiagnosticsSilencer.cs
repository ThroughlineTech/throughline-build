using System.Runtime.CompilerServices;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Codex.Tests;

// Silences worker-agent failure diagnostics for the whole test assembly. The
// agent failure-path tests deliberately drive parse failures and pass, so the
// production stderr diagnostics ("[CodexAgent] ...") would otherwise spew into
// the test runner on every green run. Set exactly once at assembly load - never
// toggled per-test - so there is no cross-test race. See WorkerDiagnostics in
// ThroughlineBuild.Workers.Common.
internal static class TestDiagnosticsSilencer
{
    [ModuleInitializer]
    internal static void Init() => WorkerDiagnostics.Write = static _ => { };
}

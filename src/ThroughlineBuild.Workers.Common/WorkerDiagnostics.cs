namespace ThroughlineBuild.Workers.Common;

/// <summary>
/// Diagnostic sink for the vendor worker agents. Failure paths in the agents
/// (parse failure, missing WORKER_RESULT envelope, executable-not-found) write a
/// one-line reason here so a real <c>build</c> run surfaces the cause on stderr
/// the moment a worker fails.
///
/// The same reason is also carried in the returned <c>WorkerResult.FailureReason</c>
/// and reprinted by the owning phase (e.g. "Implement phase failed: ..."), so this
/// sink is only the early, per-worker copy - not the sole surface for the failure.
///
/// Defaults to <see cref="System.Console.Error"/>. The worker *test* projects
/// redirect it to a no-op once at assembly load (a [ModuleInitializer]): their
/// failure-path unit tests deliberately drive these branches and pass, so without
/// the redirect every green run spews "[WorkerResultParser] ..." lines into the
/// test runner's console. The sink is set exactly once per process and never
/// toggled per-test, so - unlike the AppContext reflection switch the tests guard -
/// it carries no cross-test race.
/// </summary>
internal static class WorkerDiagnostics
{
    public static Action<string> Write { get; set; } = static msg => Console.Error.WriteLine(msg);
}

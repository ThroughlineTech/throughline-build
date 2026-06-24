namespace ThroughlineBuild.Cli;

/// <summary>
/// Thrown when the operator types 'q' / 'quit' at an interactive 'build init' prompt to bail out.
///
/// Derives from <see cref="OperationCanceledException"/> on purpose: the connected-path catch
/// guards inside InitCommand (catch (Exception ex) when (ex is not OperationCanceledException))
/// let it propagate untouched, exactly like a Ctrl-C cancellation. Program.cs catches it
/// specifically - BEFORE its OperationCanceledException catch (this type is more derived) - to
/// print "Aborted." and return exit code 5, distinct from Ctrl-C's "Cancelled." / exit 1.
/// </summary>
public sealed class InitAbortedException : OperationCanceledException
{
    public InitAbortedException() : base("Aborted by operator.") { }
}

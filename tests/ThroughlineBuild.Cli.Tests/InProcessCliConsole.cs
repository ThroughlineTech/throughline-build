using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli.Tests;

internal sealed class InProcessCliConsole(
    TextReader stdin,
    TextWriter stdout,
    TextWriter stderr) : IConsole
{
    public bool IsInputRedirected => true;
    public void WriteLine(string value) => stdout.WriteLine(value);
    public void Write(string value) => stdout.Write(value);
    public void ErrorWriteLine(string value) => stderr.WriteLine(value);
    public string? ReadLine() => stdin.ReadLine();

    public char? ReadKeyChar()
    {
        var line = stdin.ReadLine();
        return string.IsNullOrEmpty(line) ? null : line[0];
    }
}

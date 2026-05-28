namespace ThroughlineBuild.Cli;

/// <summary>
/// Thin console abstraction for testability.
/// SystemConsole wraps System.Console; tests inject a FakeConsole.
/// </summary>
public interface IConsole
{
    /// <summary>Write a line to stdout.</summary>
    void WriteLine(string value);

    /// <summary>Write to stdout without a trailing newline.</summary>
    void Write(string value);

    /// <summary>Write a line to stderr.</summary>
    void ErrorWriteLine(string value);

    /// <summary>Read a full line from stdin (blocks until newline or EOF).</summary>
    string? ReadLine();

    /// <summary>
    /// Read a single key character from stdin.
    /// When IsInputRedirected is true, falls back to reading a full line and returning its first char.
    /// </summary>
    char? ReadKeyChar();

    /// <summary>True when stdin is not connected to an interactive terminal.</summary>
    bool IsInputRedirected { get; }
}

/// <summary>
/// Production implementation that delegates to System.Console.
/// </summary>
public sealed class SystemConsole : IConsole
{
    public static readonly SystemConsole Instance = new();

    private SystemConsole() { }

    public void WriteLine(string value) => Console.WriteLine(value);
    public void Write(string value) => Console.Write(value);
    public void ErrorWriteLine(string value) => Console.Error.WriteLine(value);
    public string? ReadLine() => Console.ReadLine();
    public bool IsInputRedirected => Console.IsInputRedirected;

    public char? ReadKeyChar()
    {
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            if (line is null || line.Length == 0) return null;
            return line[0];
        }
        var key = Console.ReadKey(intercept: true);
        return key.KeyChar;
    }
}

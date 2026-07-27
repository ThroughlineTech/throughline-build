using System.Text;

namespace ThroughlineBuild.Helpers;

/// <summary>
/// A <see cref="TextWriter"/> wrapper that prepends a fixed prefix string to every
/// <see cref="WriteLine(string?)"/> call. Used to tag progress-digest output lines with
/// a ticket-ID prefix so parallel chain output is line-by-line attributable.
/// Only <see cref="WriteLine(string?)"/> is prefixed; low-level <see cref="Write(char)"/>
/// delegates without a prefix, because the only consumer path is
/// <c>options.ProgressDigestSink.WriteLine(dl)</c>.
/// </summary>
public sealed class PrefixedTextWriter : TextWriter
{
    private readonly string _prefix;
    private readonly TextWriter _inner;

    public PrefixedTextWriter(string prefix, TextWriter inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _prefix = prefix ?? string.Empty;
        _inner = inner;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);

    public override void WriteLine(string? value) => _inner.WriteLine(_prefix + value);
}

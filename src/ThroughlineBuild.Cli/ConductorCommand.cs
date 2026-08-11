using System.Text;
using ThroughlineBuild.Cli.Json;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

internal static class ConductorCommand
{
    private const string InvariantHeader = "[[conductor.review.invariants]]";
    private const string EscalationHeader = "[conductor.review.escalation]";
    private const string ScaffoldPlaceholder =
        "Replace this sentence with a true review invariant for this repository.";
    private const string PromptExamplePlaceholder =
        "State a true, repository-derived invariant here.";
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly HashSet<string> InvariantKeys = new(StringComparer.Ordinal)
    {
        "id",
        "statement",
        "paths",
        "blocks_done",
    };

    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count < 2)
            return Usage(json, output, error, "conductor subcommand is required");

        return args[1] switch
        {
            "prompt" => ExecutePrompt(args, json, output, error),
            "apply" => ExecuteApply(args, json, startDirectory, input, output, error),
            _ => Usage(json, output, error, $"unknown conductor subcommand: {args[1]}"),
        };
    }

    private static int ExecutePrompt(
        IReadOnlyList<string> args,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count != 2)
            return Usage(json, output, error, $"unknown argument for conductor prompt: {args[2]}");

        string prompt;
        try
        {
            prompt = ConductorPromptLoader.Load();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Failure(json, output, error, ex.Message);
        }

        if (json)
            CliEnvelopeWriter.WriteConductorPrompt(output, new ConductorPromptView(prompt));
        else
            output.Write(prompt);
        return 0;
    }

    private static int ExecuteApply(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count < 3 || string.IsNullOrWhiteSpace(args[2]))
            return Usage(json, output, error, "conductor apply requires <path|->");
        if (args.Count != 3)
            return Usage(json, output, error, $"unknown argument for conductor apply: {args[3]}");

        var conductorPath = SopDoctorCommand.FindConductorFile(startDirectory);
        if (conductorPath is null)
            return ConfigFailure(json, output, error, "missing .build/conductor.toml");

        string submitted;
        try
        {
            submitted = string.Equals(args[2], "-", StringComparison.Ordinal)
                ? input.ReadToEnd()
                : File.ReadAllText(Path.GetFullPath(args[2], startDirectory), Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(json, output, error, $"failed to read invariant TOML: {ex.Message}");
        }

        if (!TryValidateInvariantInput(submitted, out var validated, out var validationError))
            return Failure(json, output, error, validationError);

        byte[] original;
        try
        {
            original = File.ReadAllBytes(conductorPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigFailure(json, output, error, $"failed to read .build/conductor.toml: {ex.Message}");
        }

        if (!TryFindReplacementBounds(original, out var invariantStart, out var escalationStart, out var boundsError))
            return ConfigFailure(json, output, error, boundsError);

        var eol = DetectEol(original);
        var replacementText = validated.NormalizedToml.Replace("\n", eol, StringComparison.Ordinal) + eol + eol;
        var replacement = Utf8NoBom.GetBytes(replacementText);
        var updated = new byte[invariantStart + replacement.Length + original.Length - escalationStart];
        Buffer.BlockCopy(original, 0, updated, 0, invariantStart);
        Buffer.BlockCopy(replacement, 0, updated, invariantStart, replacement.Length);
        Buffer.BlockCopy(
            original,
            escalationStart,
            updated,
            invariantStart + replacement.Length,
            original.Length - escalationStart);

        var changed = !original.AsSpan().SequenceEqual(updated);
        if (changed && !TryAtomicWrite(conductorPath, updated, out var writeError))
            return Failure(json, output, error, writeError);

        var view = new ConductorApplyView(
            Path.GetFullPath(conductorPath),
            validated.InvariantCount,
            changed);
        if (json)
            CliEnvelopeWriter.WriteConductorApply(output, view);
        else
            output.WriteLine($"applied {view.InvariantCount} review invariants to {view.ConductorPath}");
        return 0;
    }

    private static bool TryValidateInvariantInput(
        string raw,
        out ValidatedInvariantInput validated,
        out string error)
    {
        validated = default;
        error = string.Empty;
        var normalized = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .TrimStart('\uFEFF');
        if (normalized.Length == 0)
        {
            error = "invariant TOML is empty";
            return false;
        }
        if (normalized.Contains("```", StringComparison.Ordinal))
        {
            error = "invariant input must be TOML only; Markdown fences are not allowed";
            return false;
        }

        var headerCount = 0;
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal))
                continue;
            if (!IsExactHeaderLine(trimmed, InvariantHeader))
            {
                error = $"invariant input contains an extra TOML section: {trimmed}";
                return false;
            }
            headerCount++;
        }

        if (headerCount == 0)
        {
            error = $"invariant input must contain {InvariantHeader} blocks";
            return false;
        }
        if (headerCount is < 2 or > 5)
        {
            error = "invariant input must contain between 2 and 5 invariant blocks";
            return false;
        }

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize(
                normalized,
                BuildTomlSerializerContext.Default.TomlTable)
                ?? throw new InvalidOperationException("TOML document produced no root table");
        }
        catch (Exception ex)
        {
            error = $"invariant TOML is malformed: {ex.Message}";
            return false;
        }

        if (root.Count != 1 || !root.TryGetValue("conductor", out var conductorRaw) ||
            conductorRaw is not TomlTable conductor || conductor.Count != 1 ||
            !conductor.TryGetValue("review", out var reviewRaw) ||
            reviewRaw is not TomlTable review || review.Count != 1 ||
            !review.TryGetValue("invariants", out var invariantsRaw) ||
            invariantsRaw is not TomlTableArray invariants || invariants.Count != headerCount)
        {
            error = $"input must contain only {InvariantHeader} blocks";
            return false;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < invariants.Count; i++)
        {
            var invariant = invariants[i];
            if (invariant.Keys.Any(key => !InvariantKeys.Contains(key)))
            {
                error = $"invariant block {i + 1} contains an unknown key";
                return false;
            }
            if (!TryNonEmptyString(invariant, "id", out var id))
            {
                error = $"invariant block {i + 1} requires a non-empty id";
                return false;
            }
            if (!seenIds.Add(id))
            {
                error = $"invariant id '{id}' is duplicated";
                return false;
            }
            if (!TryNonEmptyString(invariant, "statement", out var statement))
            {
                error = $"invariant block {i + 1} requires a non-empty statement";
                return false;
            }
            if (IsPlaceholder(statement))
            {
                error = $"invariant block {i + 1} still contains a placeholder statement";
                return false;
            }
            if (invariant.TryGetValue("paths", out var pathsRaw) && !IsNonEmptyStringArray(pathsRaw))
            {
                error = $"invariant block {i + 1} paths must contain only non-empty strings";
                return false;
            }
            if (invariant.TryGetValue("blocks_done", out var blocksDoneRaw) && blocksDoneRaw is not bool)
            {
                error = $"invariant block {i + 1} blocks_done must be a boolean";
                return false;
            }
        }

        validated = new ValidatedInvariantInput(normalized, invariants.Count);
        return true;
    }

    private static bool TryFindReplacementBounds(
        byte[] content,
        out int invariantStart,
        out int escalationStart,
        out string error)
    {
        invariantStart = FindHeaderLine(content, InvariantHeader, 0);
        escalationStart = invariantStart < 0
            ? -1
            : FindHeaderLine(content, EscalationHeader, invariantStart + 1);
        if (invariantStart < 0 || escalationStart < 0 || escalationStart <= invariantStart)
        {
            error =
                $".build/conductor.toml must contain a contiguous {InvariantHeader} run followed by {EscalationHeader}";
            return false;
        }

        var run = Utf8NoBom.GetString(content.AsSpan(invariantStart, escalationStart - invariantStart));
        foreach (var line in run.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                !IsExactHeaderLine(trimmed, InvariantHeader))
            {
                error = "the existing conductor invariant run is not contiguous";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static int FindHeaderLine(byte[] content, string header, int start)
    {
        var marker = Encoding.ASCII.GetBytes(header);
        var lineStart = Math.Max(0, start);
        if (lineStart > 0)
        {
            var nextLine = Array.IndexOf(content, (byte)'\n', lineStart);
            lineStart = nextLine < 0 ? content.Length : nextLine + 1;
        }

        while (lineStart < content.Length)
        {
            var valueStart = lineStart;
            while (valueStart < content.Length && content[valueStart] is (byte)' ' or (byte)'\t')
                valueStart++;
            if (valueStart + marker.Length <= content.Length &&
                content.AsSpan(valueStart, marker.Length).SequenceEqual(marker))
            {
                var remainder = valueStart + marker.Length;
                while (remainder < content.Length && content[remainder] is (byte)' ' or (byte)'\t')
                    remainder++;
                if (remainder >= content.Length || content[remainder] is (byte)'\r' or (byte)'\n' or (byte)'#')
                    return lineStart;
            }

            var newline = Array.IndexOf(content, (byte)'\n', lineStart);
            if (newline < 0)
                break;
            lineStart = newline + 1;
        }

        return -1;
    }

    private static bool IsExactHeaderLine(string line, string header)
    {
        if (!line.StartsWith(header, StringComparison.Ordinal))
            return false;
        var remainder = line[header.Length..].TrimStart();
        return remainder.Length == 0 || remainder.StartsWith('#');
    }

    private static bool TryNonEmptyString(TomlTable table, string key, out string value)
    {
        value = string.Empty;
        if (!table.TryGetValue(key, out var raw) || raw is not string text || string.IsNullOrWhiteSpace(text))
            return false;
        value = text.Trim();
        return true;
    }

    private static bool IsNonEmptyStringArray(object? raw) =>
        raw is TomlArray array && array.Count > 0 &&
        array.All(item => item is string value && !string.IsNullOrWhiteSpace(value));

    private static bool IsPlaceholder(string statement) =>
        string.Equals(statement, ScaffoldPlaceholder, StringComparison.Ordinal) ||
        string.Equals(statement, PromptExamplePlaceholder, StringComparison.Ordinal) ||
        string.Equals(statement, "TODO", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(statement, "TBD", StringComparison.OrdinalIgnoreCase) ||
        statement.Contains("placeholder", StringComparison.OrdinalIgnoreCase);

    private static string DetectEol(byte[] content)
    {
        var newline = Array.IndexOf(content, (byte)'\n');
        return newline > 0 && content[newline - 1] == (byte)'\r' ? "\r\n" : "\n";
    }

    private static bool TryAtomicWrite(string conductorPath, byte[] content, out string error)
    {
        var directory = Path.GetDirectoryName(conductorPath)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(conductorPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, conductorPath, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"failed to atomically update .build/conductor.toml: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build conductor prompt [--json]");
            error.WriteLine("       build conductor apply <path|-> [--json]");
        }
        return 2;
    }

    private static int ConfigFailure(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.ConfigError, message);
        else
            error.WriteLine($"Config error: {message}");
        return 2;
    }

    private static int Failure(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Failure, message);
        else
            error.WriteLine($"Error: {message}");
        return 1;
    }

    private readonly record struct ValidatedInvariantInput(string NormalizedToml, int InvariantCount);
}

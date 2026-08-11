using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Helpers;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

public static class CandidateStatusCommand
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: false);

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        bool json,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Count < 2)
            return Usage(json, output, error, "candidate subcommand is required");
        if (args[1] != "status")
            return Usage(json, output, error, $"unknown candidate subcommand: {args[1]}");

        var parsed = ParseOptions(args, 2, ["--ticket", "--base"]);
        if (parsed.Error is not null)
            return Usage(json, output, error, parsed.Error);
        if (!parsed.Values.TryGetValue("--ticket", out var ticket) || string.IsNullOrWhiteSpace(ticket))
            return Usage(json, output, error, "--ticket is required for candidate status");
        if (!parsed.Values.TryGetValue("--base", out var baseRef) || string.IsNullOrWhiteSpace(baseRef))
            return Usage(json, output, error, "--base is required for candidate status");

        var result = await BuildStatusAsync(ticket.Trim(), baseRef.Trim(), workingDirectory, ct)
            .ConfigureAwait(false);
        if (!result.Success)
            return Failure(json, output, error, result.ErrorCode!, result.Message!);

        if (json)
            CliEnvelopeWriter.WriteCandidateStatus(output, result.View!);
        else
            WriteHuman(output, result.View!);
        return 0;
    }

    private static async Task<CandidateStatusResult> BuildStatusAsync(
        string ticket,
        string baseRef,
        string workingDirectory,
        CancellationToken ct)
    {
        var inside = await RunGitAsync(
            workingDirectory,
            ["rev-parse", "--is-inside-work-tree"],
            ct).ConfigureAwait(false);
        if (inside.ExitCode != 0 || !string.Equals(inside.StdoutText.Trim(), "true", StringComparison.Ordinal))
            return CandidateStatusResult.Failed(
                CliErrorCodes.NotGitRepository,
                "current directory is not inside a git worktree");

        var topLevel = await RunGitAsync(
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            ct).ConfigureAwait(false);
        if (topLevel.ExitCode != 0 || string.IsNullOrWhiteSpace(topLevel.StdoutText))
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                GitFailure("git rev-parse --show-toplevel", topLevel));
        var worktreeRoot = Path.GetFullPath(topLevel.StdoutText.Trim());

        var baseShaRun = await RunGitAsync(
            worktreeRoot,
            ["rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"],
            ct).ConfigureAwait(false);
        if (baseShaRun.ExitCode != 0 || !IsFullSha(baseShaRun.StdoutText.Trim()))
            return CandidateStatusResult.Failed(
                CliErrorCodes.MissingBase,
                $"base ref '{baseRef}' did not resolve to a commit");
        var baseSha = baseShaRun.StdoutText.Trim().ToLowerInvariant();

        var headShaRun = await RunGitAsync(
            worktreeRoot,
            ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"],
            ct).ConfigureAwait(false);
        if (headShaRun.ExitCode != 0 || !IsFullSha(headShaRun.StdoutText.Trim()))
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                "HEAD did not resolve to a commit");
        var headSha = headShaRun.StdoutText.Trim().ToLowerInvariant();

        var branchRun = await RunGitAsync(
            worktreeRoot,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            ct).ConfigureAwait(false);
        string? branch = null;
        if (branchRun.ExitCode == 0)
        {
            var rawBranch = branchRun.StdoutText.Trim();
            if (rawBranch.Length > 0 && rawBranch != "HEAD")
                branch = rawBranch;
        }

        var statusRun = await RunGitAsync(
            worktreeRoot,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            ct).ConfigureAwait(false);
        if (statusRun.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                GitFailure("git status --porcelain=v1 -z", statusRun));
        var dirtyState = ParseDirtyState(statusRun.StdoutText);
        if (dirtyState.HasConflicts)
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                $"worktree has unresolved conflicts: {string.Join(", ", dirtyState.ConflictedPaths)}");

        var trackedDiff = await RunGitAsync(
            worktreeRoot,
            ["diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", baseSha, "--"],
            ct).ConfigureAwait(false);
        if (trackedDiff.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.Failure,
                GitFailure("git diff", trackedDiff));

        var cachedDiff = await RunGitAsync(
            worktreeRoot,
            ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", baseSha, "--"],
            ct).ConfigureAwait(false);
        if (cachedDiff.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.Failure,
                GitFailure("git diff --cached", cachedDiff));

        var diffNames = await RunGitAsync(
            worktreeRoot,
            ["diff", "--name-status", "-z", "--find-renames", baseSha, "--"],
            ct).ConfigureAwait(false);
        if (diffNames.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.Failure,
                GitFailure("git diff --name-status", diffNames));

        var cachedDiffNames = await RunGitAsync(
            worktreeRoot,
            ["diff", "--cached", "--name-status", "-z", "--find-renames", baseSha, "--"],
            ct).ConfigureAwait(false);
        if (cachedDiffNames.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.Failure,
                GitFailure("git diff --cached --name-status", cachedDiffNames));

        var untrackedRun = await RunGitAsync(
            worktreeRoot,
            ["ls-files", "--others", "--exclude-standard", "-z"],
            ct).ConfigureAwait(false);
        if (untrackedRun.ExitCode != 0)
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                GitFailure("git ls-files --others", untrackedRun));
        var untrackedPaths = SplitNul(untrackedRun.StdoutText);
        var untrackedHash = HashUntrackedFiles(worktreeRoot, untrackedPaths);
        if (!untrackedHash.Success)
            return CandidateStatusResult.Failed(
                CliErrorCodes.UnhashablePath,
                untrackedHash.Message!);

        var lease = ReadLease(
            worktreeRoot,
            ticket,
            ResolveConfiguredTicketPrefix(worktreeRoot));
        if (!lease.Success)
            return CandidateStatusResult.Failed(
                CliErrorCodes.InvalidWorktreeState,
                lease.Message!);

        var touchedPaths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in ParseDiffNameStatusPaths(diffNames.StdoutText))
            touchedPaths.Add(path);
        foreach (var path in ParseDiffNameStatusPaths(cachedDiffNames.StdoutText))
            touchedPaths.Add(path);
        foreach (var path in untrackedPaths)
            touchedPaths.Add(path);

        var view = new CandidateStatusView(
            Ticket: ticket,
            BaseRef: baseRef,
            BaseSha: baseSha,
            HeadSha: headSha,
            Branch: branch,
            WorkingDirectory: worktreeRoot,
            TrackedDiffHash: HashBytes(trackedDiff.StdoutBytes),
            CachedDiffHash: HashBytes(cachedDiff.StdoutBytes),
            UntrackedHash: untrackedHash.Hash!,
            TouchedPaths: touchedPaths.ToList(),
            UntrackedPaths: untrackedPaths.Order(StringComparer.Ordinal).ToList(),
            Lease: lease.View!,
            DirtyState: dirtyState.View);
        return CandidateStatusResult.Ok(view);
    }

    internal static CandidateHashResult HashUntrackedFiles(
        string worktreeRoot,
        IReadOnlyList<string> relativePaths)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
        {
            var normalized = NormalizeGitPath(relativePath);
            if (Path.IsPathRooted(normalized))
                return CandidateHashResult.Failed($"untracked path is rooted: {normalized}");
            if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
                return CandidateHashResult.Failed($"untracked path is not repository-relative: {normalized}");

            var fullPath = Path.GetFullPath(Path.Combine(
                worktreeRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrBelow(fullPath, worktreeRoot))
                return CandidateHashResult.Failed($"untracked path escapes the worktree: {normalized}");
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                return CandidateHashResult.Failed($"untracked path disappeared before hashing: {normalized}");
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.LinkTarget is not null || attributes.HasFlag(FileAttributes.ReparsePoint))
                return CandidateHashResult.Failed($"untracked path is a symlink or reparse point: {normalized}");
            if (attributes.HasFlag(FileAttributes.Directory))
                return CandidateHashResult.Failed($"untracked path is a directory, not a file: {normalized}");
            if (!fileInfo.Exists)
                return CandidateHashResult.Failed($"untracked path disappeared before hashing: {normalized}");

            byte[] contentHash;
            long length;
            var gitMode = GitModeForRegularFile(fullPath);
            try
            {
                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                length = stream.Length;
                contentHash = SHA256.HashData(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return CandidateHashResult.Failed($"untracked path could not be hashed: {normalized}: {ex.Message}");
            }

            AppendUtf8(hasher, normalized);
            hasher.AppendData([0]);
            AppendUtf8(hasher, gitMode);
            hasher.AppendData([0]);
            AppendUtf8(hasher, length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hasher.AppendData([0]);
            hasher.AppendData(contentHash);
            hasher.AppendData([0]);
        }

        return CandidateHashResult.Ok(ToSha256(hasher.GetHashAndReset()));
    }

    private static string GitModeForRegularFile(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return "100644";

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            var executable =
                mode.HasFlag(UnixFileMode.UserExecute) ||
                mode.HasFlag(UnixFileMode.GroupExecute) ||
                mode.HasFlag(UnixFileMode.OtherExecute);
            return executable ? "100755" : "100644";
        }
        catch
        {
            return "100644";
        }
    }

    private static CandidateLeaseReadResult ReadLease(
        string worktreeRoot,
        string requestedTicket,
        string? configuredTicketPrefix)
    {
        var manifestPath = Path.Combine(worktreeRoot, WorktreeLeaseConstants.ManifestFileName);
        if (!File.Exists(manifestPath))
            return CandidateLeaseReadResult.Ok(new CandidateLeaseView(
                Present: false,
                Path: manifestPath,
                TicketMatches: null,
                Manifest: null));

        WorktreeLeaseManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(
                stream,
                CliJsonContext.Default.WorktreeLeaseManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CandidateLeaseReadResult.Failed($"lease manifest could not be read: {ex.Message}");
        }

        if (manifest is null)
            return CandidateLeaseReadResult.Failed("lease manifest was empty");
        if (manifest.SchemaVersion != WorktreeLeaseConstants.ManifestSchemaVersion)
            return CandidateLeaseReadResult.Failed($"unsupported lease manifest schema version: {manifest.SchemaVersion}");
        if (!PathsEqual(manifest.WorktreePath, worktreeRoot))
            return CandidateLeaseReadResult.Failed("lease manifest worktreePath does not match the current worktree");

        return CandidateLeaseReadResult.Ok(new CandidateLeaseView(
            Present: true,
            Path: manifestPath,
            TicketMatches: TicketMatches(requestedTicket, manifest.Ticket, configuredTicketPrefix),
            Manifest: manifest));
    }

    private static CandidateDirtyParseResult ParseDirtyState(string statusOutput)
    {
        var hasTracked = false;
        var hasStaged = false;
        var hasUnstaged = false;
        var hasUntracked = false;
        var conflicts = new List<string>();
        var tokens = statusOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 3)
                continue;

            var x = token[0];
            var y = token[1];
            var path = NormalizeGitPath(token.Substring(3));
            if (x == '?' && y == '?')
            {
                hasUntracked = true;
                continue;
            }

            hasTracked = true;
            if (IsConflictCode(x, y))
                conflicts.Add(path);
            if (x != ' ' && x != '?' && x != '!')
                hasStaged = true;
            if (y != ' ' && y != '?' && y != '!')
                hasUnstaged = true;

            if ((x is 'R' or 'C') && i + 1 < tokens.Length)
                i++;
        }

        var view = new CandidateDirtyStateView(
            IsDirty: hasTracked || hasUntracked,
            HasTrackedChanges: hasTracked,
            HasStagedChanges: hasStaged,
            HasUnstagedChanges: hasUnstaged,
            HasUntrackedFiles: hasUntracked,
            HasConflicts: conflicts.Count > 0);
        return new CandidateDirtyParseResult(view, conflicts.AsReadOnly());
    }

    private static IReadOnlyList<string> ParseDiffNameStatusPaths(string output)
    {
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var i = 0; i < tokens.Length;)
        {
            var status = tokens[i++];
            if (status.Length == 0 || i >= tokens.Length)
                break;
            if (status[0] is 'R' or 'C')
            {
                paths.Add(NormalizeGitPath(tokens[i++]));
                if (i < tokens.Length)
                    paths.Add(NormalizeGitPath(tokens[i++]));
            }
            else
            {
                paths.Add(NormalizeGitPath(tokens[i++]));
            }
        }
        return paths;
    }

    private static IReadOnlyList<string> SplitNul(string text) =>
        text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeGitPath)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsConflictCode(char x, char y) =>
        x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    private static bool IsFullSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string NormalizeGitPath(string path) =>
        path.Replace('\\', '/');

    private static bool IsSameOrBelow(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static bool TicketMatches(
        string requested,
        string manifestTicket,
        string? configuredTicketPrefix)
    {
        var manifestNormalized = NormalizeTicket(manifestTicket);
        if (IsBareTicketNumber(requested))
        {
            if (string.IsNullOrEmpty(configuredTicketPrefix))
                return false;

            var resolvedRequested = NormalizeTicket($"{configuredTicketPrefix}-{requested}");
            return string.Equals(resolvedRequested, manifestNormalized, StringComparison.Ordinal);
        }

        var requestedNormalized = NormalizeTicket(requested);
        return requestedNormalized.Length > 0 &&
            string.Equals(requestedNormalized, manifestNormalized, StringComparison.Ordinal);
    }

    private static string? ResolveConfiguredTicketPrefix(string worktreeRoot)
    {
        var conductorPath = RepositoryLayout.Resolve(worktreeRoot).FindBuildDataFile("conductor.toml");
        if (conductorPath is null)
            return null;

        try
        {
            var root = TomlSerializer.Deserialize(
                File.ReadAllText(conductorPath),
                BuildTomlSerializerContext.Default.TomlTable);
            if (root is null ||
                !root.TryGetValue("conductor", out var conductorRaw) ||
                conductorRaw is not TomlTable conductor ||
                !conductor.TryGetValue("ticket_prefix", out var ticketPrefixRaw) ||
                ticketPrefixRaw is not string ticketPrefix ||
                string.IsNullOrWhiteSpace(ticketPrefix))
                return null;

            var normalizedPrefix = NormalizeTicket(ticketPrefix);
            return normalizedPrefix.Length > 0 ? normalizedPrefix : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
        {
            return null;
        }
    }

    private static bool IsBareTicketNumber(string value) =>
        value.Length > 0 && value.All(char.IsDigit);

    private static string NormalizeTicket(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string HashBytes(byte[] bytes) =>
        ToSha256(SHA256.HashData(bytes));

    private static string ToSha256(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();

    private static void AppendUtf8(IncrementalHash hasher, string value) =>
        hasher.AppendData(Encoding.UTF8.GetBytes(value));

    private static string GitFailure(string command, GitRun run)
    {
        if (run.TimedOut)
            return $"{command} timed out after {GitTimeout.TotalSeconds:0}s";
        var stderr = run.Stderr.Trim();
        return stderr.Length > 0
            ? $"{command} failed: {stderr}"
            : $"{command} failed with exit code {run.ExitCode}";
    }

    private static async Task<GitRun> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotePath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("status.refreshIndex=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        try
        {
            using var timeoutCts = new CancellationTokenSource(GitTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var linkedCt = linkedCts.Token;
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start git process");
            try { process.StandardInput.Close(); } catch { }

            await using var stdout = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, linkedCt);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCt);
            var timedOut = false;

            try
            {
                await process.WaitForExitAsync(linkedCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                {
                    try { await stdoutTask.ConfigureAwait(false); } catch { }
                    try { await stderrTask.ConfigureAwait(false); } catch { }
                    throw;
                }
                timedOut = true;
            }

            try { await stdoutTask.ConfigureAwait(false); } catch { }
            string stderr;
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { stderr = string.Empty; }
            return new GitRun(timedOut ? -1 : SafeExitCode(process), stdout.ToArray(), stderr, timedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitRun(-1, Array.Empty<byte>(), ex.Message, TimedOut: false);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private static ParsedOptions ParseOptions(
        IReadOnlyList<string> args,
        int start,
        IReadOnlyCollection<string> allowedValues)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < args.Count; i++)
        {
            var option = args[i];
            if (!allowedValues.Contains(option))
                return new ParsedOptions(values, $"unknown option: {option}");
            if (values.ContainsKey(option))
                return new ParsedOptions(values, $"option may be specified only once: {option}");
            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                return new ParsedOptions(values, $"{option} requires a value");
            values[option] = args[i];
        }
        return new ParsedOptions(values, null);
    }

    private static int Failure(
        bool json,
        TextWriter output,
        TextWriter error,
        string code,
        string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, code, message);
        else
            error.WriteLine($"Candidate status failed: {message}");
        return 1;
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build candidate status --ticket <id> --base <ref> [--json]");
        }
        return 2;
    }

    private static void WriteHuman(TextWriter output, CandidateStatusView view)
    {
        output.WriteLine($"candidate status for {view.Ticket}");
        output.WriteLine($"base: {view.BaseRef} {view.BaseSha}");
        output.WriteLine($"head: {view.HeadSha}");
        if (view.Branch is not null)
            output.WriteLine($"branch: {view.Branch}");
        output.WriteLine($"working directory: {view.WorkingDirectory}");
        output.WriteLine($"tracked diff: {view.TrackedDiffHash}");
        output.WriteLine($"cached/index diff: {view.CachedDiffHash}");
        output.WriteLine($"untracked: {view.UntrackedHash}");
        output.WriteLine($"dirty: {(view.DirtyState.IsDirty ? "yes" : "no")}");
        output.WriteLine($"lease manifest: {(view.Lease.Present ? "present" : "absent")}");
        foreach (var path in view.TouchedPaths)
            output.WriteLine($"  {path}");
    }

    private sealed record ParsedOptions(
        IReadOnlyDictionary<string, string> Values,
        string? Error);

    private sealed record GitRun(
        int ExitCode,
        byte[] StdoutBytes,
        string Stderr,
        bool TimedOut)
    {
        public string StdoutText => Utf8.GetString(StdoutBytes);
    }

    private sealed record CandidateStatusResult(
        bool Success,
        CandidateStatusView? View,
        string? ErrorCode,
        string? Message)
    {
        public static CandidateStatusResult Ok(CandidateStatusView view) =>
            new(true, view, null, null);

        public static CandidateStatusResult Failed(string code, string message) =>
            new(false, null, code, message);
    }

    internal sealed record CandidateHashResult(bool Success, string? Hash, string? Message)
    {
        public static CandidateHashResult Ok(string hash) => new(true, hash, null);
        public static CandidateHashResult Failed(string message) => new(false, null, message);
    }

    private sealed record CandidateLeaseReadResult(
        bool Success,
        CandidateLeaseView? View,
        string? Message)
    {
        public static CandidateLeaseReadResult Ok(CandidateLeaseView view) => new(true, view, null);
        public static CandidateLeaseReadResult Failed(string message) => new(false, null, message);
    }

    private sealed record CandidateDirtyParseResult(
        CandidateDirtyStateView View,
        IReadOnlyList<string> ConflictedPaths)
    {
        public bool HasConflicts => ConflictedPaths.Count > 0;
    }
}

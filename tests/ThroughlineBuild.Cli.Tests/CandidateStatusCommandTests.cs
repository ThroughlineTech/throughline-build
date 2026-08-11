using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class CandidateStatusCommandTests
{
    private const string EmptySha256 = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task CleanTree_ReportsStableHashesAndCleanDirtyState()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            var headBefore = await RunGitOutputAsync(repository, "rev-parse", "HEAD");

            var (exit, stdout, stderr) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            using var json = JsonDocument.Parse(stdout);
            var root = json.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            var data = root.GetProperty("data");
            Assert.Equal("TLB-600", data.GetProperty("ticket").GetString());
            Assert.Equal("main", data.GetProperty("baseRef").GetString());
            Assert.Equal(headBefore.Trim(), data.GetProperty("baseSha").GetString());
            Assert.Equal(headBefore.Trim(), data.GetProperty("headSha").GetString());
            Assert.Equal(EmptySha256, data.GetProperty("trackedDiffHash").GetString());
            Assert.Equal(EmptySha256, data.GetProperty("cachedDiffHash").GetString());
            Assert.Equal(EmptySha256, data.GetProperty("untrackedHash").GetString());
            Assert.Empty(data.GetProperty("touchedPaths").EnumerateArray());
            Assert.False(data.GetProperty("dirtyState").GetProperty("isDirty").GetBoolean());
            Assert.False(data.GetProperty("lease").GetProperty("present").GetBoolean());

            var headAfter = await RunGitOutputAsync(repository, "rev-parse", "HEAD");
            Assert.Equal(headBefore, headAfter);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task TrackedAndStagedChanges_ReportSeparateTrackedAndCachedHashes()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "unstaged\n");

            var (unstagedExit, unstagedStdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, unstagedExit);
            using (var json = JsonDocument.Parse(unstagedStdout))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.NotEqual(EmptySha256, data.GetProperty("trackedDiffHash").GetString());
                Assert.Equal(EmptySha256, data.GetProperty("cachedDiffHash").GetString());
                Assert.Contains("tracked.txt", JsonArrayStrings(data.GetProperty("touchedPaths")));
                var dirty = data.GetProperty("dirtyState");
                Assert.True(dirty.GetProperty("hasTrackedChanges").GetBoolean());
                Assert.False(dirty.GetProperty("hasStagedChanges").GetBoolean());
                Assert.True(dirty.GetProperty("hasUnstagedChanges").GetBoolean());
            }

            await RunGitAsync(repository, "add", "tracked.txt");

            var (stagedExit, stagedStdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, stagedExit);
            using var stagedJson = JsonDocument.Parse(stagedStdout);
            var stagedData = stagedJson.RootElement.GetProperty("data");
            Assert.NotEqual(EmptySha256, stagedData.GetProperty("trackedDiffHash").GetString());
            Assert.NotEqual(EmptySha256, stagedData.GetProperty("cachedDiffHash").GetString());
            var stagedDirty = stagedData.GetProperty("dirtyState");
            Assert.True(stagedDirty.GetProperty("hasStagedChanges").GetBoolean());
            Assert.False(stagedDirty.GetProperty("hasUnstagedChanges").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task UntrackedTextAndBinaryFiles_AffectUntrackedHashAndTouchedPaths()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "notes.txt"), "new note\n");
            await File.WriteAllBytesAsync(
                Path.Combine(repository, "binary.bin"),
                [0, 1, 2, 3, 255]);

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            var data = json.RootElement.GetProperty("data");
            Assert.NotEqual(EmptySha256, data.GetProperty("untrackedHash").GetString());
            Assert.Contains("binary.bin", JsonArrayStrings(data.GetProperty("untrackedPaths")));
            Assert.Contains("notes.txt", JsonArrayStrings(data.GetProperty("touchedPaths")));
            var dirty = data.GetProperty("dirtyState");
            Assert.True(dirty.GetProperty("hasUntrackedFiles").GetBoolean());
            Assert.False(dirty.GetProperty("hasTrackedChanges").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task IndexOnlyChange_IsIncludedInTouchedPaths()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "staged only\n");
            await RunGitAsync(repository, "add", "tracked.txt");
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "base\n");

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(EmptySha256, data.GetProperty("trackedDiffHash").GetString());
            Assert.NotEqual(EmptySha256, data.GetProperty("cachedDiffHash").GetString());
            Assert.Contains("tracked.txt", JsonArrayStrings(data.GetProperty("touchedPaths")));
            var dirty = data.GetProperty("dirtyState");
            Assert.True(dirty.GetProperty("hasStagedChanges").GetBoolean());
            Assert.True(dirty.GetProperty("hasUnstagedChanges").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task DeletedRenamedAndTrackedBinaryFiles_AreIncludedInTrackedFingerprint()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "delete-me.txt"), "delete\n");
            File.WriteAllText(Path.Combine(repository, "rename-old.txt"), "rename\n");
            await File.WriteAllBytesAsync(Path.Combine(repository, "tracked.bin"), [0, 5, 8, 0]);
            await RunGitAsync(repository, "add", "delete-me.txt", "rename-old.txt", "tracked.bin");
            await RunGitAsync(repository, "commit", "-m", "add cases");

            File.Delete(Path.Combine(repository, "delete-me.txt"));
            await RunGitAsync(repository, "mv", "rename-old.txt", "rename-new.txt");
            await File.WriteAllBytesAsync(Path.Combine(repository, "tracked.bin"), [0, 5, 9, 0]);

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            var data = json.RootElement.GetProperty("data");
            Assert.NotEqual(EmptySha256, data.GetProperty("trackedDiffHash").GetString());
            var touched = JsonArrayStrings(data.GetProperty("touchedPaths"));
            Assert.Contains("delete-me.txt", touched);
            Assert.Contains("rename-old.txt", touched);
            Assert.Contains("rename-new.txt", touched);
            Assert.Contains("tracked.bin", touched);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task TextconvHelper_IsNotRunForTrackedDiffHash()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            ConfigureTextconv(repository);
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "worktree textconv bait\n");

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.True(json.RootElement.GetProperty("data")
                .GetProperty("dirtyState")
                .GetProperty("hasUnstagedChanges")
                .GetBoolean());
            Assert.False(File.Exists(Path.Combine(repository, "textconv-ran.txt")));
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task TextconvHelper_IsNotRunForCachedDiffHash()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            ConfigureTextconv(repository);
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "cached textconv bait\n");
            await RunGitAsync(repository, "add", "tracked.txt");

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.True(json.RootElement.GetProperty("data")
                .GetProperty("dirtyState")
                .GetProperty("hasStagedChanges")
                .GetBoolean());
            Assert.False(File.Exists(Path.Combine(repository, "textconv-ran.txt")));
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_ReportsMetadataAndTicketMatch()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "TLB-600");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "TLB-600");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            var lease = json.RootElement.GetProperty("data").GetProperty("lease");
            Assert.True(lease.GetProperty("present").GetBoolean());
            Assert.True(lease.GetProperty("ticketMatches").GetBoolean());
            Assert.Equal("TLB-600", lease.GetProperty("manifest").GetProperty("ticket").GetString());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_BareTicketUsesConfiguredConductorPrefix()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "TLB-600");
            WriteConductorTicketPrefix(repository, "TLB");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "600");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.True(json.RootElement.GetProperty("data").GetProperty("lease").GetProperty("ticketMatches").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_BareTicketWithoutConfiguredPrefixDoesNotMatch()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "TLB-600");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "600");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("data").GetProperty("lease").GetProperty("ticketMatches").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_NumericManifestDoesNotBypassBareTicketResolution()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "600");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "600");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("data").GetProperty("lease").GetProperty("ticketMatches").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_ForeignPrefixWithSameDigitsDoesNotMatch()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "TLB-600");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "ABC-600");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("data").GetProperty("lease").GetProperty("ticketMatches").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task LeaseManifestPresent_DifferentTicketDigitsDoNotMatch()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            await WriteLeaseManifestAsync(repository, "TLB-600");

            var (exit, stdout, _) = await RunStatusAsync(repository, ticket: "TLB-601");

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("data").GetProperty("lease").GetProperty("ticketMatches").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task MissingBaseRef_EmitsSpecificFailureEnvelope()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            var (exit, stdout, _) = await RunStatusAsync(repository, baseRef: "does-not-exist");

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                CliErrorCodes.MissingBase,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task NonGitDirectory_EmitsSpecificFailureEnvelope()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "candidate-status-command-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (exit, stdout, _) = await RunStatusAsync(directory);

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                CliErrorCodes.NotGitRepository,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConflictedWorktree_EmitsInvalidWorktreeState()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "conflict.txt"), "base\n");
            await RunGitAsync(repository, "add", "conflict.txt");
            await RunGitAsync(repository, "commit", "-m", "conflict base");
            await RunGitAsync(repository, "switch", "-c", "side");
            File.WriteAllText(Path.Combine(repository, "conflict.txt"), "side\n");
            await RunGitAsync(repository, "commit", "-am", "side change");
            await RunGitAsync(repository, "switch", "main");
            File.WriteAllText(Path.Combine(repository, "conflict.txt"), "main\n");
            await RunGitAsync(repository, "commit", "-am", "main change");
            var merge = await RunGitAllowFailureAsync(repository, "merge", "side");
            Assert.NotEqual(0, merge);

            var (exit, stdout, _) = await RunStatusAsync(repository);

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal(
                CliErrorCodes.InvalidWorktreeState,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(
                "unresolved conflicts",
                json.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        finally
        {
            try { await RunGitAllowFailureAsync(repository, "merge", "--abort"); } catch { }
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task MissingUntrackedPath_IsReportedAsUnhashable()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            var result = CandidateStatusCommand.HashUntrackedFiles(repository, ["missing.bin"]);

            Assert.False(result.Success);
            Assert.Contains("disappeared before hashing", result.Message);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task UntrackedExecutableBit_AffectsHashOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repository = await InitializeRepositoryAsync();
        try
        {
            var scriptPath = Path.Combine(repository, "script.sh");
            File.WriteAllText(scriptPath, "echo ok\n");

            var normal = CandidateStatusCommand.HashUntrackedFiles(repository, ["script.sh"]);
            Assert.True(normal.Success);

            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);

            var executable = CandidateStatusCommand.HashUntrackedFiles(repository, ["script.sh"]);

            Assert.True(executable.Success);
            Assert.NotEqual(normal.Hash, executable.Hash);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task UntrackedSymlink_IsReportedAsUnhashableWhenSupported()
    {
        var repository = await InitializeRepositoryAsync();
        try
        {
            File.WriteAllText(Path.Combine(repository, "target.txt"), "target\n");
            var linkPath = Path.Combine(repository, "link.txt");
            try
            {
                File.CreateSymbolicLink(linkPath, "target.txt");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var result = CandidateStatusCommand.HashUntrackedFiles(repository, ["link.txt"]);

            Assert.False(result.Success);
            Assert.Contains("symlink or reparse point", result.Message);
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunStatusAsync(
        string workingDirectory,
        string ticket = "TLB-600",
        string baseRef = "main")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CandidateStatusCommand.ExecuteAsync(
            ["candidate", "status", "--ticket", ticket, "--base", baseRef],
            json: true,
            workingDirectory,
            stdout,
            stderr,
            CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static async Task WriteLeaseManifestAsync(string repository, string ticket)
    {
        var baseSha = (await RunGitOutputAsync(repository, "rev-parse", "HEAD")).Trim();
        var manifest = new WorktreeLeaseManifest(
            WorktreeLeaseConstants.ManifestSchemaVersion,
            ticket,
            "candidate-status",
            "lease/tlb-600-candidate-status",
            baseSha,
            repository,
            repository,
            Path.Combine(repository, ".worktrees", "conductor"),
            repository,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new WorktreeInstallRecord("skipped", 0));
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            CliJsonContext.Default.WorktreeLeaseManifest);
        File.WriteAllText(
            Path.Combine(repository, WorktreeLeaseConstants.ManifestFileName),
            manifestJson);
    }

    private static void WriteConductorTicketPrefix(string repository, string ticketPrefix)
    {
        var buildDirectory = Path.Combine(repository, ".build");
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(
            Path.Combine(buildDirectory, "conductor.toml"),
            $"[conductor]{Environment.NewLine}ticket_prefix = \"{ticketPrefix}\"{Environment.NewLine}");
    }

    private static IReadOnlyList<string> JsonArrayStrings(JsonElement array) =>
        array.EnumerateArray().Select(element => element.GetString() ?? string.Empty).ToList();

    private static async Task<string> InitializeRepositoryAsync()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "candidate-status-command-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.email", "test@test.com");
        await RunGitAsync(repository, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "base\n");
        await RunGitAsync(repository, "add", "tracked.txt");
        await RunGitAsync(repository, "commit", "-m", "initial");
        return repository;
    }

    private static void ConfigureTextconv(string repository)
    {
        var scriptPath = Path.Combine(repository, "textconv.sh");
        var markerPath = Path.Combine(repository, "textconv-ran.txt").Replace("\\", "/");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n" +
            $"printf ran > '{markerPath}'\n" +
            "cat \"$1\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        var configPath = scriptPath.Replace("\\", "/");
        File.WriteAllText(Path.Combine(repository, ".gitattributes"), "tracked.txt diff=mutating\n");
        RunGitAsync(repository, "config", "diff.mutating.textconv", $"sh \"{configPath}\"")
            .GetAwaiter()
            .GetResult();
    }

    private static Task RunGitAsync(string workingDirectory, params string[] args) =>
        RunGitAsync(workingDirectory, false, args);

    private static async Task<int> RunGitAllowFailureAsync(string workingDirectory, params string[] args) =>
        await RunGitAsync(workingDirectory, true, args);

    private static async Task<int> RunGitAsync(
        string workingDirectory,
        bool allowFailure,
        params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdoutText = await stdout;
        var stderrText = await stderr;
        if (!allowFailure)
        {
            Assert.True(
                process.ExitCode == 0,
                $"git {string.Join(" ", args)} failed: {stderrText}; stdout: {stdoutText}");
        }
        return process.ExitCode;
    }

    private static async Task<string> RunGitOutputAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdoutText = await stdout;
        var stderrText = await stderr;
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {stderrText}; stdout: {stdoutText}");
        return stdoutText;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Git can briefly retain Windows handles after conflict and index tests.
        }
    }
}

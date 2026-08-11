using System.Diagnostics;
using System.Text.Json;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Resolution tests that cut real git worktrees. A linked worktree's .git is a file rather
/// than a directory, which is what the hand-rolled walks these tests replaced tripped over,
/// so nothing here can be exercised with a fabricated directory tree.
/// </summary>
[Collection("Cli Tests Environment")]
public sealed class RepositoryLayoutTests
{
    private const string ValidConductorToml = """
        [conductor]
        min_build_version = "0.1.0"
        branch_prefix = "ticket"
        ticket_prefix = "TLB"
        source_roots = ["src", "tests", "docs"]
        architecture_map = "docs/throughline-build-architecture.md"
        rework_cap = 3

        [[conductor.review.invariants]]
        id = "contracts-io-free"
        statement = "ThroughlineBuild.Contracts stays free of file and network I/O."

        [conductor.review.escalation]
        model_size = "large"
        paths = ["src/ThroughlineBuild.Cli/**"]

        [constellation]
        platform = "dotnet-cli"
        contract_authority = "src/ThroughlineBuild.Contracts"
        """;

    private const string ValidReviewChecksToml = """
        [review]

        [[review.checks]]
        name = "unit"
        executable = "dotnet"
        arguments = ["test", "--no-restore"]
        role = "gating"
        """;

    [Fact]
    public async Task Layout_InLinkedWorktree_ReportsWorktreeAndMainWorktreeRoots()
    {
        var scratch = NewScratchDirectory();
        var mainRepo = Path.Combine(scratch, "main-repo");
        var worktree = Path.Combine(scratch, "linked-worktree");

        try
        {
            await InitRepositoryAsync(mainRepo);
            await RunGitAsync(mainRepo, "worktree", "add", worktree, "-b", "feature");

            var layout = RepositoryLayout.Resolve(worktree);

            Assert.True(layout.IsLinkedWorktree);
            AssertSamePath(worktree, layout.WorktreeRoot);
            AssertSamePath(mainRepo, layout.MainWorktreeRoot);
        }
        finally
        {
            TryDeleteRepository(mainRepo, scratch);
        }
    }

    [Fact]
    public async Task DoctorAndBrief_InLinkedWorktree_MatchTheMainWorktree()
    {
        var scratch = NewScratchDirectory();
        var mainRepo = Path.Combine(scratch, "main-repo");
        var worktree = Path.Combine(scratch, "linked-worktree");

        try
        {
            await InitRepositoryAsync(mainRepo);
            WriteBuildData(mainRepo);

            // Emitted stubs are tracked content, so they must be committed to reach the
            // linked worktree; .build stays machine-local to the main worktree.
            var install = SopInstaller.Run(
                "install",
                mainRepo,
                SopBundleCatalog.All,
                "0.1.0+test",
                DateTimeOffset.UtcNow);
            Assert.True(install.Passed);
            await RunGitAsync(mainRepo, "add", ".claude", ".agents");
            await RunGitAsync(mainRepo, "commit", "-m", "install sop stubs");

            await RunGitAsync(mainRepo, "worktree", "add", worktree, "-b", "feature");
            Assert.False(Directory.Exists(Path.Combine(worktree, ".build")));

            var fromMain = SopDoctorCommand.RunDoctor(mainRepo, "0.1.0+test");
            var fromWorktree = SopDoctorCommand.RunDoctor(worktree, "0.1.0+test");

            Assert.True(fromMain.Passed, Describe(fromMain));
            Assert.True(fromWorktree.Passed, Describe(fromWorktree));
            AssertSamePath(fromMain.RepositoryRoot, fromWorktree.RepositoryRoot);
            AssertSamePath(fromMain.ConductorPath!, fromWorktree.ConductorPath!);
            AssertSamePath(fromMain.ConfigPath!, fromWorktree.ConfigPath!);
            AssertSamePath(Path.Combine(mainRepo, ".build", "conductor.toml"), fromWorktree.ConductorPath!);

            // Internal consistency: a report that names a root and a config names the
            // conductor from that same .build directory, never from another tree.
            AssertSamePath(
                Path.GetDirectoryName(fromWorktree.ConfigPath!)!,
                Path.GetDirectoryName(fromWorktree.ConductorPath!)!);
            AssertSamePath(
                fromWorktree.RepositoryRoot,
                Path.GetDirectoryName(Path.GetDirectoryName(fromWorktree.ConductorPath!)!)!);

            var output = new StringWriter();
            var exit = SopBriefCommand.Execute(
                ["sop", "brief", SopBundleCatalog.RunBacklogName],
                json: true,
                worktree,
                output,
                TextWriter.Null);

            Assert.Equal(0, exit);
            using var brief = JsonDocument.Parse(output.ToString());
            var data = brief.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("ready").GetBoolean());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("sopText").GetString()));
            Assert.Equal("TLB", data.GetProperty("conductor").GetProperty("ticketPrefix").GetString());
        }
        finally
        {
            TryDeleteRepository(mainRepo, scratch);
        }
    }

    [Fact]
    public async Task FindConfigFile_RepositoryWithoutConfig_DoesNotAdoptAncestorConfig()
    {
        var scratch = NewScratchDirectory();
        var mainRepo = Path.Combine(scratch, "main-repo");

        try
        {
            // An unrelated project's config sits above the repository.
            Directory.CreateDirectory(Path.Combine(scratch, ".build"));
            File.WriteAllText(Path.Combine(scratch, ".build", "config.toml"), ValidReviewChecksToml);

            await InitRepositoryAsync(mainRepo);
            var nested = Path.Combine(mainRepo, "src", "deep");
            Directory.CreateDirectory(nested);

            Assert.Null(BuildConfigLoader.FindConfigFile(mainRepo));
            Assert.Null(BuildConfigLoader.FindConfigFile(nested));
            Assert.Null(SopDoctorCommand.FindConductorFile(nested));
        }
        finally
        {
            TryDeleteRepository(mainRepo, scratch);
        }
    }

    [Fact]
    public async Task FindConfigFile_WorktreeOutsideRepositoryTree_ResolvesItsOwnRepositoryConfig()
    {
        var scratch = NewScratchDirectory();
        var mainRepo = Path.Combine(scratch, "repositories", "main-repo");
        var elsewhere = Path.Combine(scratch, "elsewhere");
        var worktree = Path.Combine(elsewhere, "linked-worktree");

        try
        {
            // The worktree is cut outside the repository tree, under a directory that holds
            // an unrelated project's config. Adopting that config would silently borrow
            // another project's gating checks and ticket backend.
            Directory.CreateDirectory(Path.Combine(elsewhere, ".build"));
            File.WriteAllText(Path.Combine(elsewhere, ".build", "config.toml"), "[review]\n");

            await InitRepositoryAsync(mainRepo);
            WriteBuildData(mainRepo);
            await RunGitAsync(mainRepo, "worktree", "add", worktree, "-b", "feature");

            var configPath = BuildConfigLoader.FindConfigFile(worktree);

            Assert.NotNull(configPath);
            AssertSamePath(Path.Combine(mainRepo, ".build", "config.toml"), configPath!);
            AssertSamePath(
                Path.Combine(mainRepo, ".build", "conductor.toml"),
                SopDoctorCommand.FindConductorFile(worktree)!);
        }
        finally
        {
            TryDeleteRepository(mainRepo, scratch);
        }
    }

    private static void WriteBuildData(string repository)
    {
        var buildDirectory = Path.Combine(repository, ".build");
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(buildDirectory, "conductor.toml"), ValidConductorToml);
        File.WriteAllText(Path.Combine(buildDirectory, "config.toml"), ValidReviewChecksToml);
        File.WriteAllText(Path.Combine(repository, ".gitignore"), ".build/\n");
    }

    private static string Describe(SopDoctorView report) =>
        string.Join("; ", report.Findings.Select(finding => $"[{finding.Code}] {finding.Path}"));

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "repository-layout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task InitRepositoryAsync(string repository)
    {
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.email", "test@example.com");
        await RunGitAsync(repository, "config", "user.name", "Test User");
        // Pin line endings so committed stub content survives checkout into the worktree
        // byte for byte, whatever the machine's global core.autocrlf says.
        await RunGitAsync(repository, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repository, "README.md"), "# test\n");
        // Tracked, so a linked worktree cut from this commit sees them too - doctor now
        // resolves ValidConductorToml's architecture_map and source_roots against the
        // filesystem (TLB-628), and must report the same result from both trees.
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        Directory.CreateDirectory(Path.Combine(repository, "src", "ThroughlineBuild.Cli"));
        Directory.CreateDirectory(Path.Combine(repository, "tests"));
        Directory.CreateDirectory(Path.Combine(repository, "docs"));
        File.WriteAllText(Path.Combine(repository, "src", ".gitkeep"), string.Empty);
        File.WriteAllText(
            Path.Combine(repository, "src", "ThroughlineBuild.Cli", "placeholder.cs"), "// test\n");
        File.WriteAllText(Path.Combine(repository, "tests", ".gitkeep"), string.Empty);
        File.WriteAllText(
            Path.Combine(repository, "docs", "throughline-build-architecture.md"), "architecture\n");
        await RunGitAsync(repository, "add", "README.md", "src", "tests", "docs");
        await RunGitAsync(repository, "commit", "-m", "initial commit");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed with exit {process.ExitCode}: {error}");
    }

    private static void AssertSamePath(string expected, string actual) =>
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            ignoreCase: OperatingSystem.IsWindows());

    private static void TryDeleteRepository(string repository, string scratch)
    {
        try
        {
            if (Directory.Exists(repository))
            {
                using var process = Process.Start(new ProcessStartInfo("git")
                {
                    Arguments = "worktree prune",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = repository,
                });
                process?.WaitForExit();
            }
        }
        catch (Exception ex) when (ex is SystemException)
        {
            // best effort
        }

        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows keeps git object files locked briefly; leaving scratch behind is harmless.
        }
    }
}

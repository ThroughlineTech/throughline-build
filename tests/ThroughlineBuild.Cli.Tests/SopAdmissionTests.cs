using System.Diagnostics;
using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class SopAdmissionTests
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
        id = "aot-json"
        statement = "CLI JSON output uses source-generated JsonSerializerContext."
        paths = ["src/ThroughlineBuild.Cli/**"]
        blocks_done = true

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
    public async Task SopBrief_AdmissionMode_EmitsResolvedRootShaEnvironmentAndVerbPolicy()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        await InitializeGitRepositoryAsync(repo);
        var sha = await RunGitOutputAsync(repo, "rev-parse", "HEAD");

        try
        {
            var result = await RunCliInDirectoryAsync(
                repo,
                ["sop", "brief", "run-backlog", "admission", repo, sha, "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.Equal(string.Empty, result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            var runMode = doc.RootElement.GetProperty("data").GetProperty("runMode");
            Assert.Equal("admission", runMode.GetProperty("mode").GetString());
            Assert.Equal(sha.ToLowerInvariant(), runMode.GetProperty("inspectionSha").GetString());
            Assert.Equal(Path.GetFullPath(repo), runMode.GetProperty("inspectionRoot").GetString());

            var environment = runMode.GetProperty("environment").EnumerateArray().ToList();
            Assert.Contains(environment, item =>
                item.GetProperty("name").GetString() == SopAdmission.RunModeEnvironmentVariable &&
                item.GetProperty("value").GetString() == "admission");
            Assert.Contains(environment, item =>
                item.GetProperty("name").GetString() == SopAdmission.InspectionShaEnvironmentVariable &&
                item.GetProperty("value").GetString() == sha.ToLowerInvariant());
            Assert.Contains(environment, item =>
                item.GetProperty("name").GetString() == SopAdmission.InspectionRootEnvironmentVariable &&
                item.GetProperty("value").GetString() == Path.GetFullPath(repo));

            var policy = runMode.GetProperty("verbPolicy");
            Assert.True(policy.GetProperty("readOnlyVerbsAllowed").GetBoolean());
            Assert.False(policy.GetProperty("worktreeLeaseAllowed").GetBoolean());
            Assert.False(policy.GetProperty("worktreeTeardownAllowed").GetBoolean());
            Assert.False(policy.GetProperty("ticketTransitionAllowed").GetBoolean());
            Assert.False(policy.GetProperty("ticketCommentAllowed").GetBoolean());
            Assert.False(policy.GetProperty("commitAllowed").GetBoolean());
            Assert.False(policy.GetProperty("branchAllowed").GetBoolean());
            Assert.False(policy.GetProperty("pushAllowed").GetBoolean());
            Assert.False(policy.GetProperty("parentOrEpicExpansionAllowed").GetBoolean());
            Assert.Contains("build get", Strings(policy.GetProperty("allowedBuildVerbs")));
            Assert.Contains("build worktree lease", Strings(policy.GetProperty("refusedBuildVerbs")));
            Assert.Contains("build transition", Strings(policy.GetProperty("refusedBuildVerbs")));
            Assert.Contains("build chain", Strings(policy.GetProperty("refusedBuildVerbs")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SopBrief_AdmissionEnvironmentWithoutModeArgs_InheritsAdmissionRunMode()
    {
        var repo = CreateRepo();
        await InitializeGitRepositoryAsync(repo);
        var sha = await RunGitOutputAsync(repo, "rev-parse", "HEAD");

        try
        {
            using var _ = SetAdmissionEnvironment(repo, sha);
            var result = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(0, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            var runMode = doc.RootElement.GetProperty("data").GetProperty("runMode");
            Assert.Equal("admission", runMode.GetProperty("mode").GetString());
            Assert.Equal(sha.ToLowerInvariant(), runMode.GetProperty("inspectionSha").GetString());
            Assert.Equal(Path.GetFullPath(repo), runMode.GetProperty("inspectionRoot").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SopBrief_AdmissionMode_RefusesRelativeRootBeforeDoctorReads()
    {
        var repo = CreateRepoWithoutConductor();

        try
        {
            var result = await RunCliInDirectoryAsync(
                repo,
                [
                    "sop",
                    "brief",
                    "run-backlog",
                    "admission",
                    ".",
                    "0123456789abcdef0123456789abcdef01234567",
                    "--json",
                ]);

            Assert.Equal(2, result.Exit);
            Assert.Equal(string.Empty, result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(CliErrorCodes.Usage, ErrorCode(doc));
            Assert.Contains("absolute path", ErrorMessage(doc), StringComparison.Ordinal);
            Assert.False(doc.RootElement.TryGetProperty("data", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SopBrief_AdmissionMode_RefusesShortShaBeforeDoctorReads()
    {
        var repo = CreateRepoWithoutConductor();

        try
        {
            var result = await RunCliInDirectoryAsync(
                repo,
                ["sop", "brief", "run-backlog", "admission", repo, "abcdef1", "--json"]);

            Assert.Equal(2, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(CliErrorCodes.Usage, ErrorCode(doc));
            Assert.Contains("40-character", ErrorMessage(doc), StringComparison.Ordinal);
            Assert.False(doc.RootElement.TryGetProperty("data", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SopBrief_AdmissionMode_RefusesUnresolvableFullSha()
    {
        var repo = CreateRepoWithoutConductor();
        await InitializeGitRepositoryAsync(repo);

        try
        {
            var result = await RunCliInDirectoryAsync(
                repo,
                [
                    "sop",
                    "brief",
                    "run-backlog",
                    "admission",
                    repo,
                    "0123456789abcdef0123456789abcdef01234567",
                    "--json",
                ]);

            Assert.Equal(2, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(CliErrorCodes.Usage, ErrorCode(doc));
            Assert.Contains("does not resolve", ErrorMessage(doc), StringComparison.Ordinal);
            Assert.False(doc.RootElement.TryGetProperty("data", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData("worktree lease --ticket TLB-616 --slug admission", "worktree_lease")]
    [InlineData("worktree teardown --ticket TLB-616", "worktree_teardown")]
    [InlineData("comment TLB-616 body", "ticket_comment")]
    [InlineData("transition TLB-616 InProgress", "ticket_transition")]
    [InlineData("ship TLB-616", "git_mutation")]
    [InlineData("chain TLB-616 --dry-run --max-depth 1", "scope_expansion")]
    [InlineData("sop install", "sop_mutation")]
    [InlineData("new --print-template", "mutation")]
    [InlineData("amend TLB-616 --note note", "mutation")]
    [InlineData("close TLB-616 reason", "mutation")]
    [InlineData("defer TLB-616 reason", "mutation")]
    [InlineData("reopen TLB-616", "mutation")]
    [InlineData("relate TLB-616 --list", "mutation")]
    [InlineData("setup --check", "mutation")]
    [InlineData("sweep", "mutation")]
    [InlineData("gate", "mutation")]
    [InlineData("init --print-template", "mutation")]
    [InlineData("settarget main", "mutation")]
    [InlineData("user-guide --print-template", "mutation")]
    [InlineData("op-doc spec --print", "mutation")]
    [InlineData("models refresh", "mutation")]
    public async Task AdmissionEnvironment_RefusesForbiddenMutationCategories(
        string command,
        string expectedCategory)
    {
        var repo = CreateRepoWithoutConductor();

        try
        {
            using var _ = SetAdmissionEnvironment(repo);
            var args = SplitCommand(command).Append("--json").ToArray();
            var result = await RunCliInDirectoryAsync(repo, args);

            Assert.Equal(1, result.Exit);
            Assert.Equal(string.Empty, result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(CliErrorCodes.SopAdmissionRefused, ErrorCode(doc));
            Assert.Contains(expectedCategory, ErrorMessage(doc), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task AdmissionEnvironment_AllowsSopDoctorReadOnlyVerb()
    {
        var repo = CreateRepo();

        try
        {
            using var _ = SetAdmissionEnvironment(repo);
            var result = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.Equal(string.Empty, result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    private static string CreateRepo() => CreateRepo(ValidConductorToml, ValidReviewChecksToml);

    private static string CreateRepoWithoutConductor() => CreateRepo(null, ValidReviewChecksToml);

    private static string CreateRepo(string? conductorToml, string? configToml)
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "sop-admission-tests",
            Guid.NewGuid().ToString("N"));
        var buildDir = Path.Combine(repository, ".build");
        Directory.CreateDirectory(buildDir);
        if (conductorToml is not null)
            File.WriteAllText(Path.Combine(buildDir, "conductor.toml"), conductorToml);
        if (configToml is not null)
            File.WriteAllText(Path.Combine(buildDir, "config.toml"), configToml);
        return repository;
    }

    private static async Task InitializeGitRepositoryAsync(string repository)
    {
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.email", "test@test.com");
        await RunGitAsync(repository, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "tracked");
        await RunGitAsync(repository, "add", "tracked.txt");
        await RunGitAsync(repository, "commit", "-m", "initial");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCliInDirectoryAsync(
        string directory,
        string[] args)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(directory);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static IDisposable SetAdmissionEnvironment(string root, string? sha = null) =>
        new EnvironmentScope(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [SopAdmission.RunModeEnvironmentVariable] = SopAdmission.ModeName,
                [SopAdmission.InspectionShaEnvironmentVariable] =
                    sha ?? "0123456789abcdef0123456789abcdef01234567",
                [SopAdmission.InspectionRootEnvironmentVariable] = Path.GetFullPath(root),
            });

    private static IReadOnlyList<string> SplitCommand(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> Strings(JsonElement element) =>
        element.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();

    private static string ErrorCode(JsonDocument doc) =>
        doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    private static string ErrorMessage(JsonDocument doc) =>
        doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunProcessAsync(workingDirectory, "git", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Stderr);
    }

    private static async Task<string> RunGitOutputAsync(string workingDirectory, params string[] args)
    {
        var result = await RunProcessAsync(workingDirectory, "git", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Stderr);
        return result.Stdout.Trim();
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> args)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _oldValues;

        public EnvironmentScope(IReadOnlyDictionary<string, string?> newValues)
        {
            _oldValues = newValues.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var pair in newValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        public void Dispose()
        {
            foreach (var pair in _oldValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

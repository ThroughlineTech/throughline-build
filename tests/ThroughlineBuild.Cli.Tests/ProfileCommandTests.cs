using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class ProfileCommandTests
{
    private sealed class RecordingVerifier(ProfileGateVerificationResult result) : ProfileGateVerifier
    {
        public int CallCount { get; private set; }

        public override Task<ProfileGateVerificationResult> VerifyAsync(
            ProjectProfile profile,
            string mainWorktreePath,
            IGitClient git,
            AutomatedChecksRunner runner,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private const string ProfileJson = """
    {
      "language": "example",
      "framework": "example-stack",
      "package_manager": "tool",
      "build_command": "tool build",
      "test_command": "tool test",
      "review_checks": [
        {
          "name": "build",
          "executable": "tool",
          "arguments": ["build"],
          "timeout_minutes": 5,
          "role": "gating",
          "required_paths": ["project.file"],
          "canary": [{ "path": "src/__probe.txt", "content": "broken" }]
        }
      ]
    }
    """;

    [Fact]
    public async Task ApplyFromStdin_VerifiesCanaryAndReportsTrue()
    {
        var verifier = new RecordingVerifier(ProfileGateVerificationResult.Ok());
        var result = await RunInRepositoryAsync(
            "# profile apply consumes only valid TOML\n",
            ["profile", "apply", "-", "--json"],
            stdin: ProfileJson,
            verifier: verifier);

        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(1, verifier.CallCount);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("changed").GetBoolean());
        Assert.True(data.GetProperty("canaryVerified").GetBoolean());
        Assert.Contains("canaries_verified = true", result.ConfigText);
        Assert.Contains("executable = \"tool\"", result.ConfigText);
    }

    [Fact]
    public async Task ApplyRejectsVacuousCheckWithoutWritingConfig()
    {
        const string initial = "# unchanged\n";
        var verifier = new RecordingVerifier(ProfileGateVerificationResult.Fail(
            "gating check 'build' is structurally vacuous"));

        var result = await RunInRepositoryAsync(
            initial,
            ["profile", "apply", "-", "--json"],
            stdin: ProfileJson,
            verifier: verifier);

        Assert.Equal(1, result.Exit);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(initial, result.ConfigText);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("structurally vacuous",
            json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplySkipCanaryWarnsRecordsFalseAndDoesNotVerify()
    {
        var verifier = new RecordingVerifier(ProfileGateVerificationResult.Fail("must not be called"));

        var result = await RunInRepositoryAsync(
            "# minimal\n",
            ["profile", "apply", "-", "--skip-canary", "--json"],
            stdin: ProfileJson,
            verifier: verifier);

        Assert.Equal(0, result.Exit);
        Assert.Equal(0, verifier.CallCount);
        Assert.Contains("Warning:", result.Stderr);
        Assert.Contains("were not proven capable of rejecting their canaries", result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("data").GetProperty("canaryVerified").GetBoolean());
        Assert.Contains("canaries_verified = false", result.ConfigText);
    }

    [Fact]
    public async Task ApplyRejectsGatingCheckWithMissingCanary()
    {
        var verifier = new RecordingVerifier(ProfileGateVerificationResult.Fail(
            "gating check 'build' has no canary; cannot prove it is non-vacuous"));
        var profileWithoutCanary = ProfileJson.Replace(
            ",\n          \"canary\": [{ \"path\": \"src/__probe.txt\", \"content\": \"broken\" }]",
            "",
            StringComparison.Ordinal);

        var result = await RunInRepositoryAsync(
            "# unchanged\n",
            ["profile", "apply", "-", "--json"],
            stdin: profileWithoutCanary,
            verifier: verifier);

        Assert.Equal(1, result.Exit);
        Assert.Equal("# unchanged\n", result.ConfigText);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Contains("has no canary",
            json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplyRejectsMalformedJson()
    {
        var result = await RunInRepositoryAsync(
            "# valid but minimal\n",
            ["profile", "apply", "-", "--json"],
            stdin: "{ not JSON }");

        Assert.Equal(2, result.Exit);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("# valid but minimal\n", result.ConfigText);
    }

    [Fact]
    public async Task PromptEmitsWithoutRepositoryConfigurationOrCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "profile-prompt-tests", Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.CreateDirectory(directory);
            Directory.SetCurrentDirectory(directory);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exit = await CliApplication.RunAsync(
                ["profile", "prompt", "--json"],
                (_, _) => throw new InvalidOperationException("worker must not be constructed"),
                new InProcessCliConsole(TextReader.Null, stdout, stderr));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            using var json = JsonDocument.Parse(stdout.ToString());
            var prompt = json.RootElement.GetProperty("data").GetProperty("prompt").GetString();
            Assert.Contains("Interrogate the repository itself", prompt);
            Assert.Contains("Return ONLY the JSON object", prompt);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ApplyIsIdempotentAndExitsNonzeroOnDifferingChecksUntilForced()
    {
        var initial = "# minimal config\n";
        var first = await RunInRepositoryAsync(initial, ["profile", "apply", "-"], stdin: ProfileJson,
            keepRepository: true);
        try
        {
            Assert.Equal(0, first.Exit);

            // Re-applying the same profile is a no-op success, not a failure (TLB-639).
            var second = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-"], ProfileJson);
            Assert.Equal(0, second.Exit);
            Assert.Equal(first.ConfigText, second.ConfigText);
            Assert.Equal(string.Empty, second.Stderr);

            var customized = """
            [review]

            [[review.checks]]
            name = "custom"
            executable = "npm"
            arguments = ["test"]
            timeout_minutes = 5
            """;
            File.WriteAllText(Path.Combine(first.Repository!, ".build", "config.toml"), customized);

            var skipped = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-"], ProfileJson);
            Assert.Equal(1, skipped.Exit);
            Assert.Equal(customized, skipped.ConfigText);
            Assert.Contains("--force", skipped.Stderr);
            Assert.DoesNotContain("--force-profile", skipped.Stderr);

            var forced = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-", "--force"], ProfileJson);
            Assert.Equal(0, forced.Exit);
            Assert.Contains("executable = \"tool\"", forced.ConfigText);
        }
        finally
        {
            TryDeleteDirectory(first.Repository!);
        }
    }

    [Fact]
    public async Task RemovedDeriveExitsUsageAndPointsToPromptAndApply()
    {
        var result = await RunInRepositoryAsync("# unchanged\n", ["profile", "derive"]);

        Assert.Equal(2, result.Exit);
        Assert.Contains("profile derive was removed", result.Stderr);
        Assert.Contains("profile prompt", result.Stderr);
        Assert.Contains("profile apply", result.Stderr);
        Assert.Equal("# unchanged\n", result.ConfigText);
    }

    [Fact]
    public void VerifyCanariesIsExplicitAndDoesNotAcceptApplyForceFlag()
    {
        Assert.True(ProfileCommand.TryParse(
            ["profile", "verify-canaries", "profile.json"], out var invocation, out var error), error);
        Assert.Equal(ProfileAction.VerifyCanaries, invocation!.Action);
        Assert.Equal("profile.json", invocation.InputPath);

        Assert.False(ProfileCommand.TryParse(
            ["profile", "verify-canaries", "profile.json", "--force"], out _, out var forceError));
        Assert.Contains("does not accept --force", forceError);

        Assert.False(ProfileCommand.TryParse(
            ["profile", "verify-canaries", "profile.json", "--skip-canary"], out _, out var skipError));
        Assert.Contains("does not accept --skip-canary", skipError);
    }

    private static async Task<(int Exit, string Stdout, string Stderr, string ConfigText, string? Repository)> RunInRepositoryAsync(
        string config,
        string[] args,
        string? stdin = null,
        bool keepRepository = false,
        ProfileGateVerifier? verifier = null)
    {
        var repository = Path.Combine(Path.GetTempPath(), "profile-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repository, ".build"));
        await RunGitAsync(repository, "init");
        File.WriteAllText(Path.Combine(repository, ".build", "config.toml"), config);
        var result = await RunAtRepositoryAsync(repository, args, stdin, verifier);
        if (!keepRepository)
        {
            TryDeleteDirectory(repository);
            return (result.Exit, result.Stdout, result.Stderr, result.ConfigText, null);
        }

        return (result.Exit, result.Stdout, result.Stderr, result.ConfigText, repository);
    }

    private static async Task<(int Exit, string Stdout, string Stderr, string ConfigText, string? Repository)> RunAtRepositoryAsync(
        string repository,
        string[] args,
        string? stdin,
        ProfileGateVerifier? verifier = null)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(repository);
            if (stdin is not null)
                Console.SetIn(new StringReader(stdin));
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"),
                new InProcessCliConsole(
                    stdin is null ? TextReader.Null : new StringReader(stdin),
                    stdout,
                    stderr),
                verifier ?? new RecordingVerifier(ProfileGateVerificationResult.Ok()));
            var config = File.ReadAllText(Path.Combine(repository, ".build", "config.toml"));
            return (exit, stdout.ToString(), stderr.ToString(), config, repository);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
        }
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

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
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
        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)} failed: {await stderr}; stdout: {await stdout}");
    }
}

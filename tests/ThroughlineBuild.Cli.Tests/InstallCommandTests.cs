using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class InstallCommandTests
{
    private const string ProfileJson = """
    {
      "language": "TypeScript",
      "framework": "Vite",
      "package_manager": "pnpm",
      "build_command": "pnpm build",
      "test_command": "pnpm test",
      "review_checks": [
        {
          "name": "web-test",
          "executable": "pnpm",
          "arguments": ["test"],
          "timeout_minutes": 5,
          "role": "gating",
          "canary": [{ "path": "app/canary.txt", "content": "broken" }]
        }
      ],
      "contract_authority": "app"
    }
    """;

    private const string InvariantsToml = """
    [[conductor.review.invariants]]
    id = "browser-contract"
    statement = "Browser entry points preserve their public navigation contract."
    paths = ["app/**"]
    blocks_done = true

    [[conductor.review.invariants]]
    id = "asset-boundary"
    statement = "Static assets remain isolated from application source modules."
    paths = ["public/**"]
    blocks_done = false
    """;

    private static readonly string[] StubPaths =
    [
        ".agents/skills/run-backlog/SKILL.md",
        ".claude/commands/run-backlog.md",
    ];

    [Fact]
    public void Parse_ExposesExactlyThreeResumableStages()
    {
        Assert.True(InstallCommand.TryParse(["install"], out var first, out _));
        Assert.Null(first!.ProfilePath);
        Assert.Null(first.InvariantsPath);

        Assert.True(InstallCommand.TryParse(["install", "--profile", "-"], out var second, out _));
        Assert.Equal("-", second!.ProfilePath);

        Assert.True(InstallCommand.TryParse(["install", "--invariants", "invariants.toml"], out var third, out _));
        Assert.Equal("invariants.toml", third!.InvariantsPath);

        Assert.False(InstallCommand.TryParse(
            ["install", "--profile", "p.json", "--invariants", "i.toml"], out _, out var error));
        Assert.Contains("not both", error);
    }

    // The profile-apply refusal tells the operator to re-run with --force, so the command that
    // prints it has to accept --force. See TLB-639.
    [Fact]
    public void Parse_AcceptsForceOnTheProfileStageOnly()
    {
        Assert.True(InstallCommand.TryParse(
            ["install", "--profile", "p.json", "--force"], out var forced, out var parseError), parseError);
        Assert.Equal("p.json", forced!.ProfilePath);
        Assert.True(forced.Force);

        Assert.True(InstallCommand.TryParse(["install", "--profile", "p.json"], out var plain, out _));
        Assert.False(plain!.Force);

        Assert.False(InstallCommand.TryParse(["install", "--force"], out _, out var bareError));
        Assert.Contains("applies only to --profile", bareError);

        Assert.False(InstallCommand.TryParse(
            ["install", "--invariants", "i.toml", "--force"], out _, out var invariantsError));
        Assert.Contains("applies only to --profile", invariantsError);
    }

    [Fact]
    public void Parse_ForwardsInitConfigurationOnTheFirstStageOnly()
    {
        Assert.True(InstallCommand.TryParse(
            ["install", "--no-interactive", "--plane-url", "https://plane.example", "--workspace", "team",
             "--project-id", "project-id", "--token-env", "PLANE_API_TOKEN"], out var invocation, out var error), error);

        Assert.NotNull(invocation!.Init);
        Assert.True(invocation.Init!.NoInteractive);
        Assert.Equal("https://plane.example", invocation.Init.PlaneUrl);
        Assert.Equal("team", invocation.Init.Workspace);
        Assert.Equal("project-id", invocation.Init.ProjectId);
        Assert.Equal("PLANE_API_TOKEN", invocation.Init.TokenEnv);
        Assert.True(invocation.Init.HasCompleteNonInteractiveConfiguration);

        Assert.False(InstallCommand.TryParse(
            ["install", "--profile", "profile.json", "--no-interactive"], out _, out var stageError));
        Assert.Contains("first install invocation", stageError);
    }

    [Fact]
    public async Task ExecuteAsync_NonInteractiveWithoutConfig_FailsBeforeWritingPlaceholders()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = await InstallCommand.ExecuteAsync(
                new InstallInvocation(null, null), true, repo, new StringReader(string.Empty), stdout, stderr,
                CancellationToken.None, inputRedirected: true);

            Assert.Equal(2, exit);
            Assert.False(Directory.Exists(Path.Combine(repo, ".build")));
            using var envelope = System.Text.Json.JsonDocument.Parse(stdout.ToString());
            var message = envelope.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains("build install --no-interactive", message);
            Assert.Contains("--token-env PLANE_API_TOKEN", message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public void JsonHandoffEnvelope_IsSourceGeneratedAndMachineReadable()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var output = new StringWriter();

        CliEnvelopeWriter.WriteInstall(output, new InstallView(
            "profile_handoff", false, "STOP: agent handoff required", "prompt", "next", null, null, []));

        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("profile_handoff", json.RootElement.GetProperty("data").GetProperty("stage").GetString());
        Assert.Equal("prompt", json.RootElement.GetProperty("data").GetProperty("prompt").GetString());
    }

    // TLB-638: before this, only stage 1 (via CliBootstrap) ever called ResolveSecrets. Stage 3 -
    // the invocation that actually reports READY - never re-checked that the token still resolved,
    // so a token reachable only because THIS run happened to inherit an interactive shell's env
    // (rather than from config, a token file, or an env var an agent's non-interactive shell would
    // also see) could still reach READY. Remove the ResolveSecrets call added to stage 3 in
    // InstallCommand.ExecuteAsync and this test starts asserting "ready" instead of failing.
    [Fact]
    public async Task ExecuteAsync_StageThree_FailsClosedWhenPlaneTokenIsUnresolvable()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var readinessCalls = 0;
            var dependencies = EndToEndDependencies(() => readinessCalls++);

            Assert.Equal(0, (await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies)).Exit);

            Write(repo, ".build/profile.json", ProfileJson);
            Assert.Equal(0, (await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies)).Exit);

            // Drop the inline token that let stages 1-2 resolve secrets, leaving only
            // plane_api_token_env pointing at a variable this process never sets - the same shape
            // the bug report's non-interactive agent harness sees when the token lives only in an
            // interactive shell's rc file.
            var configPath = Path.Combine(repo, ".build", "config.toml");
            var withoutInlineToken = File.ReadAllText(configPath)
                .Replace("plane_api_token = \"test-plane-token\"\n", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plane_api_token = ", withoutInlineToken, StringComparison.Ordinal);
            File.WriteAllText(configPath, withoutInlineToken);

            Write(repo, ".build/invariants.toml", InvariantsToml);
            var third = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);

            Assert.NotEqual(0, third.Exit);
            Assert.Equal(0, readinessCalls);
            using var envelope = System.Text.Json.JsonDocument.Parse(third.Stdout);
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("missing_secret", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
            var message = envelope.RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("UNUSED_TOKEN", message);
            Assert.Contains("plane_api_token_file", message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllThreeStages_ReachesReadyNonInteractivelyWithoutAnEditorAndRerunsIdempotently()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var readinessCalls = 0;
            var init = new InstallInitOptions(
                NoInteractive: true,
                PlaneUrl: "https://plane.invalid",
                Workspace: "workspace",
                ProjectId: "project-id",
                TokenEnv: "PLANE_API_TOKEN");
            var dependencies = EndToEndDependencies(
                () => readinessCalls++,
                received => Assert.Equal(init, received));

            var first = await ExecuteStageAsync(
                repo, new InstallInvocation(null, null, Init: init), dependencies, inputRedirected: true);
            Assert.Equal(0, first.Exit);
            Assert.Equal("profile_handoff", Stage(first.Stdout));
            Assert.Contains(".build/profile.json", first.Stdout);
            Assert.True(File.Exists(Path.Combine(repo, ".gitignore")));

            Write(repo, ".build/profile.json", ProfileJson);
            var second = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);
            Assert.Equal(0, second.Exit);
            Assert.Equal("invariants_handoff", Stage(second.Stdout));
            Assert.Contains(".build/invariants.toml", second.Stdout);
            var conductorAfterProfile = File.ReadAllText(Path.Combine(repo, ".build/conductor.toml"));
            Assert.Contains("platform = \"Vite\"", conductorAfterProfile);
            Assert.Contains("contract_authority = \"app\"", conductorAfterProfile);

            // TLB-627: MinimalConfig's literal plane_api_token is only a stage-1 bootstrap
            // convenience. A real install never reaches "ready" with a literal token in a tracked
            // config.toml - doctor now rejects it - so swap to the safe file form (still resolving
            // to the same token) before the readiness stage that is about to commit the file.
            ReplaceInlineTokenWithFileForm(repo);

            Write(repo, ".build/invariants.toml", InvariantsToml);
            var third = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);
            Assert.Equal(0, third.Exit);
            Assert.Equal("ready", Stage(third.Stdout));
            Assert.Equal(1, readinessCalls);
            Assert.Equal("run/backlog", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal(string.Empty, await GitAsync(repo, "status", "--porcelain"));
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var expected = SopBundleCatalog.All.SelectMany(entry => entry.OwnedPaths)
                .Where(path => path.Class == SopBundleCatalog.EmittedPathClass)
                .Select(path => path.Path)
                .Append(".gitignore")
                // .build/config.toml is tracked (TLB-627): it is a new, untracked file on this fresh
                // repo, so readiness commits it alongside the stubs and .gitignore.
                .Append(".build/config.toml")
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected, committed.Order(StringComparer.Ordinal));

            var head = await GitAsync(repo, "rev-parse", "HEAD");
            var rerun = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);
            Assert.Equal(0, rerun.Exit);
            Assert.Equal("ready", Stage(rerun.Stdout));
            Assert.Equal(2, readinessCalls);
            Assert.Equal(head, await GitAsync(repo, "rev-parse", "HEAD"));
            Assert.Equal(string.Empty, await GitAsync(repo, "status", "--porcelain"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    // TLB-639: stage 2 used to refuse its own output on every stack whose executables were not
    // "dotnet", so the sequence could not be re-run. Both a non-dotnet and a dotnet toolchain must
    // now reach the same handoff twice, leaving config.toml and conductor.toml byte-identical and
    // adding no commit.
    [Theory]
    [InlineData("pnpm")]
    [InlineData("dotnet")]
    public async Task ExecuteAsync_StageTwoRerun_IsIdempotentOnAnyToolchain(string executable)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var dependencies = EndToEndDependencies(() => { });
            Assert.Equal(0, (await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies)).Exit);

            var profile = ProfileJson
                .Replace("\"pnpm\"", $"\"{executable}\"", StringComparison.Ordinal);
            Write(repo, ".build/profile.json", profile);
            var invocation = new InstallInvocation(".build/profile.json", null);

            var first = await ExecuteStageAsync(repo, invocation, dependencies);
            Assert.Equal(0, first.Exit);
            Assert.Equal("invariants_handoff", Stage(first.Stdout));
            var configAfterFirst = ReadBytesIfPresent(repo, ".build/config.toml");
            var conductorAfterFirst = ReadBytesIfPresent(repo, ".build/conductor.toml");
            var head = await GitAsync(repo, "rev-parse", "HEAD");
            var branch = await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD");

            var second = await ExecuteStageAsync(repo, invocation, dependencies);

            Assert.Equal(0, second.Exit);
            Assert.Equal("invariants_handoff", Stage(second.Stdout));
            Assert.Equal(configAfterFirst, ReadBytesIfPresent(repo, ".build/config.toml"));
            Assert.Equal(conductorAfterFirst, ReadBytesIfPresent(repo, ".build/conductor.toml"));
            Assert.Equal(head, await GitAsync(repo, "rev-parse", "HEAD"));
            Assert.Equal(branch, await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task ExecuteAsync_StageTwoSopConflict_RollsBackAndReportsRecoveryDetails()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var dependencies = EndToEndDependencies(() => { });
            Assert.Equal(0, (await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies)).Exit);
            var configBefore = ReadBytesIfPresent(repo, ".build/config.toml");

            Write(repo, ".build/profile.json", ProfileJson);
            Write(repo, ".claude/commands/run-backlog.md", "retired slash command\n");
            var failed = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null));

            Assert.NotEqual(0, failed.Exit);
            Assert.Equal(configBefore, ReadBytesIfPresent(repo, ".build/config.toml"));
            Assert.False(File.Exists(Path.Combine(repo, ".build", "conductor.toml")));
            using (var failure = System.Text.Json.JsonDocument.Parse(failed.Stdout))
            {
                var message = failure.RootElement.GetProperty("error").GetProperty("message").GetString()!;
                Assert.Contains(".claude/commands/run-backlog.md", message);
                Assert.Contains("emitted file differs from the catalog", message);
                Assert.Contains("build install --profile .build/profile.json", message);
            }

            var humanFailure = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), json: false);
            Assert.NotEqual(0, humanFailure.Exit);
            Assert.Contains(".claude/commands/run-backlog.md", humanFailure.Stderr);
            Assert.Contains("emitted file differs from the catalog", humanFailure.Stderr);
            Assert.Contains("build install --profile .build/profile.json", humanFailure.Stderr);

            File.Delete(Path.Combine(repo, ".claude", "commands", "run-backlog.md"));
            var recovered = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);
            Assert.Equal(0, recovered.Exit);
            Assert.Equal("invariants_handoff", Stage(recovered.Stdout));
            Assert.Contains("platform = \"Vite\"", File.ReadAllText(Path.Combine(repo, ".build", "conductor.toml")));

            ReplaceInlineTokenWithFileForm(repo);
            Write(repo, ".build/invariants.toml", InvariantsToml);
            var ready = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);
            Assert.Equal(0, ready.Exit);
            Assert.Equal("ready", Stage(ready.Stdout));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    // Checks a human wrote are still protected, and the --force the refusal names is reachable from
    // the installer that printed it.
    [Fact]
    public async Task ExecuteAsync_StageTwo_PreservesHandWrittenChecksUntilForced()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var dependencies = EndToEndDependencies(() => { });
            Assert.Equal(0, (await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies)).Exit);

            var configPath = Path.Combine(repo, ".build", "config.toml");
            File.WriteAllText(configPath, File.ReadAllText(configPath) + """

            [[review.checks]]
            name = "hand-written"
            executable = "make"
            arguments = ["check"]
            timeout_minutes = 5

            """);
            var handWritten = File.ReadAllBytes(configPath);

            Write(repo, ".build/profile.json", ProfileJson);
            var refused = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);

            Assert.Equal(1, refused.Exit);
            Assert.Contains("--force", refused.Stdout);
            Assert.Equal(handWritten, File.ReadAllBytes(configPath));

            var forced = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null, Force: true), dependencies);

            Assert.Equal(0, forced.Exit);
            Assert.Equal("invariants_handoff", Stage(forced.Stdout));
            Assert.Contains("executable = \"pnpm\"", File.ReadAllText(configPath));
            Assert.DoesNotContain("hand-written", File.ReadAllText(configPath));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPlatformLeavesStageTwoStateUnchangedAndCorrectedProfileResumes()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var sopCalls = 0;
            var baseline = EndToEndDependencies(() => { });
            var dependencies = baseline with
            {
                InstallSop = async (cwd, diagnostics, ct) =>
                {
                    sopCalls++;
                    return await baseline.InstallSop(cwd, diagnostics, ct);
                },
            };

            var first = await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies);
            Assert.Equal(0, first.Exit);
            var ownedPaths = StubPaths
                .Append(".build/config.toml")
                .Append(".build/conductor.toml")
                .Append(".build/sop-manifest.json")
                .ToArray();
            var before = ownedPaths.ToDictionary(
                path => path,
                path => ReadBytesIfPresent(repo, path),
                StringComparer.Ordinal);

            var emptyProfile = ProfileJson
                .Replace("\"language\": \"TypeScript\"", "\"language\": \"  \"", StringComparison.Ordinal)
                .Replace("\"framework\": \"Vite\"", "\"framework\": \"\"", StringComparison.Ordinal);
            Write(repo, ".build/profile.json", emptyProfile);
            var failed = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);

            Assert.NotEqual(0, failed.Exit);
            Assert.Contains("both empty", failed.Stdout);
            Assert.Equal(0, sopCalls);
            foreach (var path in ownedPaths)
                Assert.Equal(before[path], ReadBytesIfPresent(repo, path));

            Write(repo, ".build/profile.json", ProfileJson);
            var corrected = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);

            Assert.Equal(0, corrected.Exit);
            Assert.Equal("invariants_handoff", Stage(corrected.Stdout));
            Assert.Equal(1, sopCalls);
            Assert.Contains("platform = \"Vite\"",
                File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData("SwiftUI", "Swift", "SwiftUI")]
    [InlineData("", "Swift", "Swift")]
    public async Task ResolveGeneratedPlatform_UsesFrameworkThenLanguage(
        string framework, string language, string expected)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, ".build/conductor.toml", "[constellation]\nplatform = \"unknown\"\ncontract_authority = \"app\"\n");
            var result = InstallCommand.ResolveGeneratedPlatform(repo, framework, language);
            Assert.True(result.Success, result.Message);
            Assert.Contains($"platform = \"{expected}\"", File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveGeneratedPlatform_FailsWhenProfileValuesAreEmpty()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, ".build/conductor.toml", "[constellation]\nplatform = \"unknown\"\n");
            var result = InstallCommand.ResolveGeneratedPlatform(repo, "  ", "");
            Assert.False(result.Success);
            Assert.Contains("both empty", result.Message);
            Assert.Contains("platform = \"unknown\"", File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveGeneratedPlatform_PreservesExistingNonPlaceholderBytes()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string existing = "[constellation]\r\nplatform = \"ios-custom\"\r\ncontract_authority = \"app\"\r\n";
            Write(repo, ".build/conductor.toml", existing);
            var before = File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml"));
            var result = InstallCommand.ResolveGeneratedPlatform(repo, "SwiftUI", "Swift");
            Assert.True(result.Success, result.Message);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    // TLB-628: ResolveContractAuthority mirrors ResolveGeneratedPlatform's shape exactly, but a
    // blank profile value is a legitimate "this repository has none" answer, not a failure.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveContractAuthority_BlankValueSkipsWithNoFileChange(string contractAuthority)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string existing = "[constellation]\r\nplatform = \"unknown\"\r\ncontract_authority = \"UNRESOLVED_CONTRACT_AUTHORITY\"\r\n";
            Write(repo, ".build/conductor.toml", existing);
            var before = File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml"));
            var result = InstallCommand.ResolveContractAuthority(repo, contractAuthority);
            Assert.True(result.Success, result.Message);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveContractAuthority_ReplacesScaffoldPlaceholder()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, ".build/conductor.toml",
                "[constellation]\nplatform = \"unknown\"\ncontract_authority = \"UNRESOLVED_CONTRACT_AUTHORITY\"\n");
            var result = InstallCommand.ResolveContractAuthority(repo, "packages/shared-types");
            Assert.True(result.Success, result.Message);
            Assert.Contains(
                "contract_authority = \"packages/shared-types\"",
                File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveContractAuthority_PreservesExistingNonPlaceholderValue()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string existing = "[constellation]\r\nplatform = \"unknown\"\r\ncontract_authority = \"already-set\"\r\n";
            Write(repo, ".build/conductor.toml", existing);
            var before = File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml"));
            var result = InstallCommand.ResolveContractAuthority(repo, "packages/shared-types");
            Assert.True(result.Success, result.Message);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Readiness_FromProtectedBranch_CommitsExactlyStubsAndIsIdempotent()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, StubPaths[0], "codex stub\n");
            Write(repo, StubPaths[1], "claude stub\n");

            var first = await ReadyAsync(repo);

            Assert.True(first.Success, first.Message);
            Assert.Equal("run/backlog", first.Branch);
            Assert.Equal(string.Empty, first.Porcelain);
            Assert.Equal("run/backlog", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(StubPaths.Order(StringComparer.Ordinal), committed.Order(StringComparer.Ordinal));

            var head = await GitAsync(repo, "rev-parse", "HEAD");
            var second = await ReadyAsync(repo);
            Assert.True(second.Success, second.Message);
            Assert.Equal(head, await GitAsync(repo, "rev-parse", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Readiness_CommitsOnlyProvablySetupOwnedGitignoreWithStubs(bool trackedAtHead)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            string? headGitignore = null;
            if (trackedAtHead)
            {
                headGitignore = "repository-specific/\n";
                Write(repo, ".gitignore", headGitignore);
                await GitAsync(repo, "add", ".gitignore");
                await GitAsync(repo, "commit", "-m", "add repository ignore");
            }
            Write(repo, ".gitignore", GitignoreManager.Merge(headGitignore)!);
            Write(repo, StubPaths[0], "codex stub\n");
            Write(repo, StubPaths[1], "claude stub\n");

            var result = await ReadyAsync(repo);

            Assert.True(result.Success, result.Message);
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(StubPaths.Append(".gitignore").Order(StringComparer.Ordinal),
                committed.Order(StringComparer.Ordinal));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_BlocksGitignoreContainingPreexistingUserChangePlusSetupMerge()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string head = "repository-specific/\n";
            Write(repo, ".gitignore", head);
            await GitAsync(repo, "add", ".gitignore");
            await GitAsync(repo, "commit", "-m", "add repository ignore");
            Write(repo, ".gitignore", GitignoreManager.Merge(head + "user-change/\n")!);
            var expected = await GitAsync(repo, "status", "--porcelain");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Equal(expected + "\n", result.Porcelain);
            Assert.Contains("differs from the deterministic build setup merge over HEAD", result.Message);
            Assert.Equal("main", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_DirtyTreeFailsWithExactPorcelainAndBranch()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, "unrelated.txt", "do not commit\n");
            var expected = await GitAsync(repo, "status", "--porcelain");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Equal("main", result.Branch);
            Assert.Equal(expected + "\n", result.Porcelain);
            Assert.Contains($"branch=main", result.Message);
            Assert.Contains(expected, result.Message);
            Assert.Equal("main", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_MergeInProgressBlocks()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var gitDir = await GitAsync(repo, "rev-parse", "--git-dir");
            File.WriteAllText(Path.Combine(repo, gitDir, "MERGE_HEAD"), new string('a', 40) + "\n");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Contains("interrupted merge", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData("rebase-merge")]
    [InlineData("rebase-apply")]
    public async Task Readiness_RebaseStateBlocks(string marker)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var gitDir = await GitAsync(repo, "rev-parse", "--git-dir");
            Directory.CreateDirectory(Path.Combine(repo, gitDir, marker));

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Contains("interrupted rebase", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_WorktreeQueryFailureBlocksReady()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var result = await ReadyAsync(repo, _ => throw new IOException("lease root unavailable"));

            Assert.False(result.Success);
            Assert.Contains("worktree list query failed", result.Message);
            Assert.Contains("lease root unavailable", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private static Task<InstallReadinessResult> ReadyAsync(
        string repo,
        Func<CancellationToken, Task>? query = null) =>
        InstallReadiness.PrepareAndAssertAsync(
            repo,
            "main",
            "run/backlog",
            StubPaths,
            ".worktrees/conductor",
            [],
            string.Empty,
            CancellationToken.None,
            query ?? (_ => Task.CompletedTask));

    private static InstallDependencies EndToEndDependencies(
        Action readinessCalled,
        Action<InstallInitOptions?>? initialized = null) => new(
        async (repo, init, _, _, diagnostics, ct) =>
        {
            initialized?.Invoke(init);
            Write(repo, ".build/config.toml", MinimalConfig);
            return await new SetupCommand(
                new FullyProvisionedFake(),
                new FileSystemLocalRepoOps(repo)).ExecuteAsync(false, new TestConsole(diagnostics), ct);
        },
        (repo, _, _) =>
        {
            var result = SopInstaller.Run(
                "install", repo, SopBundleCatalog.All, BuildVersion.Current, DateTimeOffset.UtcNow,
                conductorIdentity: new SopConductorIdentity("WEB", ["app", "public"], "ticket", "README.md"));
            return Task.FromResult(new InstallSopResult(result.Passed ? 0 : 1, result));
        },
        repo => SopDoctorCommand.RunDoctor(repo, BuildVersion.Current),
        async (repo, protectedBranch, stubPaths, config, ct) =>
        {
            readinessCalled();
            return await InstallReadiness.PrepareAndAssertAsync(
                repo, protectedBranch, "run/backlog", stubPaths,
                config.Worktree.Root, config.Worktree.SeedFiles, config.Project.InstallCommand,
                ct, _ => Task.CompletedTask);
        });

    private static async Task<(int Exit, string Stdout, string Stderr)> ExecuteStageAsync(
        string repo,
        InstallInvocation invocation,
        InstallDependencies? dependencies = null,
        bool json = true,
        bool inputRedirected = false)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await InstallCommand.ExecuteAsync(
            invocation, json, repo, new StringReader(string.Empty), stdout, stderr,
            CancellationToken.None, dependencies, inputRedirected);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Stage(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("data").GetProperty("stage").GetString()!;
    }

    private const string MinimalConfig = """
    [ticketing]
    backend = "plane"
    plane_base_url = "https://plane.invalid"
    plane_workspace_slug = "workspace"
    plane_project_id = "project-id"
    plane_project_identifier = "WEB"
    plane_api_token = "test-plane-token"
    plane_api_token_env = "UNUSED_TOKEN"

    [llm]
    default_model = "unused"
    anthropic_api_key_env = "UNUSED_ANTHROPIC"

    [workers]
    default_agent = "codex"
    timeout_minutes = 5

    [workers.codex]
    executable = "codex"

    [workers.codex.sizes]
    small = { model = "unused" }
    medium = { model = "unused" }
    large = { model = "unused" }

    [events]
    log_directory = ".build/events"

    [ship]
    base_branch = "main"

    [worktree]
    root = ".worktrees/conductor"
    """;

    private sealed class FullyProvisionedFake : ITicketingProvisioner
    {
        public Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingState>>(WorkspaceSchema.States
                .Select((state, index) => new ExistingState(state.Name, state.Group, index)).ToList());
        public Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(WorkspaceSchema.Labels.ToList());
        public Task CreateStateAsync(string name, string group, double sequence, CancellationToken ct) =>
            throw new InvalidOperationException("already provisioned");
        public Task CreateLabelAsync(string name, CancellationToken ct) =>
            throw new InvalidOperationException("already provisioned");
    }

    private sealed class TestConsole(TextWriter diagnostics) : IConsole
    {
        public bool IsInputRedirected => false;
        public string? ReadLine() => null;
        public char? ReadKeyChar() => null;
        public void Write(string value) => diagnostics.Write(value);
        public void WriteLine(string value) => diagnostics.WriteLine(value);
        public void ErrorWriteLine(string value) => diagnostics.WriteLine(value);
    }

    // Replaces MinimalConfig's literal plane_api_token with plane_api_token_file, pointing at a
    // gitignored file holding the same value, so resolution keeps working without a literal token
    // ending up in the tracked config.toml (TLB-627).
    private static void ReplaceInlineTokenWithFileForm(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "secrets"));
        File.WriteAllText(Path.Combine(repo, "secrets", "plane-api-token"), "test-plane-token");
        var configPath = Path.Combine(repo, ".build", "config.toml");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace(
            "plane_api_token = \"test-plane-token\"",
            "plane_api_token_file = \"secrets/plane-api-token\"",
            StringComparison.Ordinal));
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var repo = Path.Combine(Path.GetTempPath(), "install-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init", "-b", "main");
        await GitAsync(repo, "config", "user.name", "Install Test");
        await GitAsync(repo, "config", "user.email", "install@example.invalid");
        Write(repo, "README.md", "non-dotnet fixture\n");
        // The fake identity EndToEndDependencies scaffolds with ("app"/"public" source_roots,
        // README.md architecture_map) must resolve against real paths now that doctor checks them
        // against the filesystem (TLB-628).
        Write(repo, "app/.gitkeep", string.Empty);
        Write(repo, "public/.gitkeep", string.Empty);
        await GitAsync(repo, "add", "README.md", "app", "public");
        await GitAsync(repo, "commit", "-m", "initial");
        return repo;
    }

    private static void Write(string repo, string relativePath, string content)
    {
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static byte[]? ReadBytesIfPresent(string repo, string relativePath)
    {
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static async Task<string> GitAsync(string repo, params string[] args)
    {
        var result = await new InstallGit(repo).RunAsync(CancellationToken.None, args);
        Assert.True(result.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout.TrimEnd('\r', '\n');
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}

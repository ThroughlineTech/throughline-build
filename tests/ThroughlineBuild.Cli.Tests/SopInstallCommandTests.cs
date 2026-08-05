using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class SopInstallCommandTests
{
    [Fact]
    public void Catalog_EmittedPathHashChangesRequireTrustedPreviousHistory()
    {
        var pinnedCurrentHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".claude/commands/run-backlog.md"] = "3c5a332a7504fa59cdd6653fe525a7e9e888bc4b0fe1a42e255f18914d689231",
            [".agents/skills/run-backlog/SKILL.md"] = "ab63e2b4e8ded304152970cb1c3e47cbe97570736946db4f72c5d28453133f77",
            [".claude/commands/cross-impact.md"] = "48bceacc9b28d3f6f8dcdd7776716344936038473f001b545055ec12be23b86a",
            [".agents/skills/cross-impact/SKILL.md"] = "ba33b78666fcb9cf0745c6f42dde0d48e60d72830ffe8e45908f6620dfa0c764",
        };

        foreach (var path in SopBundleCatalog.All.SelectMany(entry => entry.OwnedPaths)
                     .Where(path => path.Class == SopBundleCatalog.EmittedPathClass))
        {
            Assert.True(
                pinnedCurrentHashes.TryGetValue(path.Path, out var pinnedHash),
                $"pin a release-history guard hash for {path.Path}");
            Assert.NotNull(path.PreviousContentHashes);
            if (!string.Equals(path.ExpectedContentHash, pinnedHash, StringComparison.Ordinal))
            {
                Assert.Contains(
                    pinnedHash,
                    path.PreviousContentHashes);
            }
        }
    }

    [Fact]
    public async Task Install_IsIdempotentAndReportsNoChangeOnSecondRun()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();

        try
        {
            var first = await RunCliInDirectoryAsync(repo, ["sop", "install", "--json"]);
            Assert.Equal(0, first.Exit);
            using (var doc = JsonDocument.Parse(first.Stdout))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("changed").GetBoolean());
                Assert.Contains("created", ResultStatuses(data));
                Assert.Contains("scaffolded", ResultStatuses(data));
            }

            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.True(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/cross-impact.md")));
            Assert.True(File.Exists(PathFor(repo, ".agents/skills/cross-impact/SKILL.md")));
            Assert.True(File.Exists(PathFor(repo, ".build/conductor.toml")));
            Assert.True(File.Exists(PathFor(repo, ".build/sop-manifest.json")));

            var second = await RunCliInDirectoryAsync(repo, ["sop", "install", "--json"]);

            Assert.Equal(0, second.Exit);
            Assert.Equal(string.Empty, second.Stderr);
            using var secondDoc = JsonDocument.Parse(second.Stdout);
            var secondData = secondDoc.RootElement.GetProperty("data");
            Assert.True(secondDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(secondData.GetProperty("changed").GetBoolean());
            Assert.DoesNotContain("created", ResultStatuses(secondData));
            Assert.DoesNotContain("scaffolded", ResultStatuses(secondData));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task InitThenSopInstall_LeavesDoctorFailingOnUnresolvedScaffoldValues()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();

        try
        {
            var init = await RunCliInDirectoryAsync(repo, ["init", "--no-interactive"]);
            Assert.Equal(0, init.Exit);

            var install = await RunCliInDirectoryAsync(
                repo,
                ["sop", "install", "--sop", "run-backlog", "--json"]);
            Assert.Equal(0, install.Exit);

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);

            Assert.Equal(1, doctor.Exit);
            Assert.Equal(string.Empty, doctor.Stderr);
            using var doc = JsonDocument.Parse(doctor.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("passed").GetBoolean());
            var codes = data.GetProperty("findings")
                .EnumerateArray()
                .Select(item => item.GetProperty("code").GetString())
                .ToList();
            Assert.Contains("conductor.ticket_prefix.placeholder", codes);
            Assert.Contains("conductor.review.invariants.statement.placeholder", codes);
            Assert.Contains("constellation.platform.placeholder", codes);
            Assert.Contains("review.checks.empty", codes);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void StubResources_AreThinFailClosedAndHostBodiesMatch()
    {
        var runBacklogClaude = SopResourceLoader.LoadResource(SopBundleCatalog.RunBacklogClaudeCommandResource);
        var runBacklogCodex = SopResourceLoader.LoadResource(SopBundleCatalog.RunBacklogCodexSkillResource);
        var crossImpactClaude = SopResourceLoader.LoadResource(SopBundleCatalog.CrossImpactClaudeCommandResource);
        var crossImpactCodex = SopResourceLoader.LoadResource(SopBundleCatalog.CrossImpactCodexSkillResource);

        Assert.Equal(runBacklogClaude, StripCodexFrontmatter(runBacklogCodex));
        Assert.Equal(crossImpactClaude, StripCodexFrontmatter(crossImpactCodex));
        AssertThinFailClosedStub(runBacklogClaude, "run-backlog");
        AssertThinFailClosedStub(crossImpactClaude, "cross-impact");

        Assert.Contains(SopBundleCatalog.RunBacklog.OwnedPaths, path =>
            path.Path == ".claude/commands/run-backlog.md" &&
            path.Class == SopBundleCatalog.EmittedPathClass);
        Assert.DoesNotContain(SopBundleCatalog.RunBacklog.OwnedPaths, path =>
            path.Path.StartsWith(".claude/skills/", StringComparison.Ordinal));
        Assert.Contains(SopBundleCatalog.RunBacklog.OwnedPaths, path =>
            path.Path == ".agents/skills/run-backlog/SKILL.md" &&
            path.Class == SopBundleCatalog.EmittedPathClass);
    }

    [Fact]
    public async Task Install_NeverOverwritesExistingScaffoldedFile()
    {
        var repo = CreateRepo();
        var conductorPath = PathFor(repo, ".build/conductor.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(conductorPath)!);
        var editedConductor = ValidConductorToml.Replace("ticket_prefix = \"TLB\"", "ticket_prefix = \"CUSTOM\"");
        File.WriteAllText(conductorPath, editedConductor);

        try
        {
            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.Equal(editedConductor, File.ReadAllText(conductorPath));
            using var doc = JsonDocument.Parse(result.Stdout);
            var statuses = ResultStatuses(doc.RootElement.GetProperty("data"));
            Assert.Contains("preserved_scaffolded", statuses);
            Assert.DoesNotContain("scaffolded", statuses);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task Status_ReportsModifiedCatalogStubAsDrift()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var stub = PathFor(repo, ".agents/skills/run-backlog/SKILL.md");
            File.WriteAllText(stub, "locally changed\n");

            var status = await RunCliInDirectoryAsync(repo, ["sop", "status", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, status.Exit);
            using var doc = JsonDocument.Parse(status.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("passed").GetBoolean());
            Assert.Contains("modified", ResultStatuses(data));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task Status_ReportsHandDeletedCatalogPathAndInstallRestoresIt()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var stub = PathFor(repo, ".claude/commands/run-backlog.md");
            File.Delete(stub);

            var status = await RunCliInDirectoryAsync(repo, ["sop", "status", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, status.Exit);
            using (var doc = JsonDocument.Parse(status.Stdout))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.False(data.GetProperty("passed").GetBoolean());
                Assert.Contains("missing", ResultStatuses(data));
            }

            var restored = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, restored.Exit);
            Assert.True(File.Exists(stub));
            using var restoredDoc = JsonDocument.Parse(restored.Stdout);
            Assert.Contains("created", ResultStatuses(restoredDoc.RootElement.GetProperty("data")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Upgrade_RewritesOnlyPreviousCatalogContentAndPreservesLocalEdits()
    {
        var repo = CreateRepo();
        var current = SopResourceLoader.LoadResource(SopBundleCatalog.RunBacklogClaudeCommandResource);
        var currentHash = SopResourceLoader.ComputeSha256(current);
        var oldCatalog = "old catalog content\n";
        var oldHash = SopResourceLoader.ComputeSha256(oldCatalog);
        var custom = new SopCatalogEntry(
            "custom",
            SopBundleCatalog.RunBacklogTicketTransactionResource,
            [],
            [
                new SopOwnedPath(
                    ".claude/commands/custom.md",
                    SopBundleCatalog.EmittedPathClass,
                    currentHash,
                    SopBundleCatalog.RunBacklogClaudeCommandResource,
                    [oldHash]),
            ]);

        try
        {
            var target = PathFor(repo, ".claude/commands/custom.md");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, oldCatalog);
            WriteManifest(repo, "custom", ".claude/commands/custom.md", oldHash);

            var upgraded = SopInstaller.Run("upgrade", repo, [custom], "0.1.0+test", DateTimeOffset.UtcNow);

            Assert.True(upgraded.Passed);
            Assert.True(upgraded.Changed);
            Assert.Contains(upgraded.Results, result => result.Status == "upgraded");
            Assert.Equal(current, File.ReadAllText(target));

            File.WriteAllText(target, "local edit\n");
            WriteManifest(
                repo,
                "custom",
                ".claude/commands/custom.md",
                SopResourceLoader.ComputeSha256("local edit\n"));

            var edited = SopInstaller.Run("upgrade", repo, [custom], "0.1.0+test", DateTimeOffset.UtcNow);

            Assert.False(edited.Passed);
            Assert.False(edited.Changed);
            Assert.Contains(edited.Results, result => result.Status == "modified");
            Assert.Equal("local edit\n", File.ReadAllText(target));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task Install_IgnoresTamperedDuplicateManifestRowsWithoutCrashing()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var manifestPath = PathFor(repo, ".build/sop-manifest.json");
            File.WriteAllText(
                manifestPath,
                """
                {
                  "schemaVersion": 1,
                  "installedByBuildVersion": "tampered",
                  "updatedAtUtc": "2026-08-02T00:00:00Z",
                  "sops": [
                    {
                      "name": "run-backlog",
                      "installedByBuildVersion": "tampered",
                      "installedAtUtc": "2026-08-02T00:00:00Z",
                      "paths": [
                        {
                          "path": ".claude/commands/run-backlog.md",
                          "class": "emitted",
                          "contentHash": "bad",
                          "resourceName": "x"
                        }
                      ]
                    },
                    {
                      "name": "run-backlog",
                      "installedByBuildVersion": "tampered",
                      "installedAtUtc": "2026-08-02T00:00:00Z",
                      "paths": []
                    }
                  ]
                }
                """);

            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(data.GetProperty("changed").GetBoolean());
            Assert.Contains("manifest_ignored", ResultStatuses(data));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task Uninstall_RemovesOnlyMatchingEmittedFilesAndPreservesEditedFiles()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var edited = PathFor(repo, ".claude/commands/run-backlog.md");
            var clean = PathFor(repo, ".agents/skills/run-backlog/SKILL.md");
            File.WriteAllText(edited, "local edit\n");

            var result = await RunCliInDirectoryAsync(repo, ["sop", "uninstall", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            var statuses = ResultStatuses(doc.RootElement.GetProperty("data"));
            Assert.Contains("modified", statuses);
            Assert.Contains("removed", statuses);
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
            Assert.True(File.Exists(edited));
            Assert.False(File.Exists(clean));

            var manifestPaths = ManifestPaths(repo, "run-backlog");
            Assert.Contains(".claude/commands/run-backlog.md", manifestPaths);
            Assert.Contains(".build/conductor.toml", manifestPaths);
            Assert.DoesNotContain(".agents/skills/run-backlog/SKILL.md", manifestPaths);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task Uninstall_IsIdempotentAfterCleanRemoval()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);

            var first = await RunCliInDirectoryAsync(repo, ["sop", "uninstall", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, first.Exit);
            using (var firstDoc = JsonDocument.Parse(first.Stdout))
            {
                var firstData = firstDoc.RootElement.GetProperty("data");
                Assert.True(firstData.GetProperty("changed").GetBoolean());
                Assert.Contains("removed", ResultStatuses(firstData));
            }

            var second = await RunCliInDirectoryAsync(repo, ["sop", "uninstall", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, second.Exit);
            using var secondDoc = JsonDocument.Parse(second.Stdout);
            var secondData = secondDoc.RootElement.GetProperty("data");
            Assert.True(secondDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(secondData.GetProperty("changed").GetBoolean());
            Assert.Contains("already_absent", ResultStatuses(secondData));
            Assert.Contains("preserved_scaffolded", ResultStatuses(secondData));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SopOptionNarrowsInstallScope()
    {
        var repo = CreateRepo();

        try
        {
            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.True(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            Assert.False(File.Exists(PathFor(repo, ".claude/commands/cross-impact.md")));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/cross-impact/SKILL.md")));
            using var doc = JsonDocument.Parse(result.Stdout);
            var scope = doc.RootElement.GetProperty("data").GetProperty("scopeSops").EnumerateArray()
                .Select(item => item.GetString())
                .ToList();
            Assert.Equal(["run-backlog"], scope);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task HostOptionNarrowsEmittedStubsButKeepsSharedScaffold()
    {
        var repo = CreateRepo();

        try
        {
            var result = await RunCliInDirectoryAsync(
                repo,
                ["sop", "install", "--sop", "run-backlog", "--host", "claude", "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            Assert.True(File.Exists(PathFor(repo, ".build/conductor.toml")));

            var manifestPaths = ManifestPaths(repo, "run-backlog");
            Assert.Contains(".claude/commands/run-backlog.md", manifestPaths);
            Assert.Contains(".build/conductor.toml", manifestPaths);
            Assert.DoesNotContain(".agents/skills/run-backlog/SKILL.md", manifestPaths);

            var second = await RunCliInDirectoryAsync(
                repo,
                ["sop", "install", "--sop", "run-backlog", "--host", "codex", "--json"]);

            Assert.Equal(0, second.Exit);
            Assert.True(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            var mergedManifestPaths = ManifestPaths(repo, "run-backlog");
            Assert.Contains(".claude/commands/run-backlog.md", mergedManifestPaths);
            Assert.Contains(".agents/skills/run-backlog/SKILL.md", mergedManifestPaths);
            Assert.Contains(".build/conductor.toml", mergedManifestPaths);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task UnknownHostOptionFailsUsage()
    {
        var repo = CreateRepo();

        try
        {
            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--host", "vim", "--json"]);

            Assert.Equal(2, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("usage", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task SymlinkedCatalogPathIsRefusedBeforeAnyWrite()
    {
        var repo = CreateRepo();
        var linkPath = PathFor(repo, ".claude/commands/run-backlog.md");
        var targetPath = Path.Combine(repo, "outside-target.md");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(targetPath, "target\n");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("changed").GetBoolean());
            Assert.Contains("unsafe_path", ResultStatuses(data));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task StatusRefusesSymlinkedCatalogPathRatherThanFollowingIt()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var linkPath = PathFor(repo, ".claude/commands/run-backlog.md");
            var targetPath = Path.Combine(repo, "outside-target.md");
            File.Delete(linkPath);
            File.WriteAllText(targetPath, "target\n");

            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var result = await RunCliInDirectoryAsync(repo, ["sop", "status", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Contains("unsafe_path", ResultStatuses(doc.RootElement.GetProperty("data")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task DanglingSymlinkedCatalogPathIsRefusedBeforeAnyWrite()
    {
        var repo = CreateRepo();
        var linkPath = PathFor(repo, ".claude/commands/run-backlog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, "missing-target.md");
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var result = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);

            Assert.Equal(1, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("changed").GetBoolean());
            Assert.Contains("unsafe_path", ResultStatuses(data));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void OutOfRootCatalogPathIsRefusedBeforeAnyWrite()
    {
        var repo = CreateRepo();
        var current = SopResourceLoader.LoadResource(SopBundleCatalog.RunBacklogClaudeCommandResource);
        var entry = new SopCatalogEntry(
            "bad",
            SopBundleCatalog.RunBacklogTicketTransactionResource,
            [],
            [
                new SopOwnedPath(
                    "../outside.md",
                    SopBundleCatalog.EmittedPathClass,
                    SopResourceLoader.ComputeSha256(current),
                    SopBundleCatalog.RunBacklogClaudeCommandResource),
            ]);

        try
        {
            var result = SopInstaller.Run("install", repo, [entry], "0.1.0+test", DateTimeOffset.UtcNow);

            Assert.False(result.Passed);
            Assert.False(result.Changed);
            Assert.Contains(result.Results, item => item.Status == "unsafe_path");
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(repo)!, "outside.md")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

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

        [conductor.review.escalation]
        model_size = "large"
        paths = ["src/**"]

        [constellation]
        platform = "dotnet-cli"
        contract_authority = "src/ThroughlineBuild.Contracts"
        """;

    private static string CreateRepo()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "sop-install-tests",
            Guid.NewGuid().ToString("N"),
            "repo");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        return repository;
    }

    private static string PathFor(string repository, string relativePath) =>
        Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteManifest(
        string repository,
        string sop,
        string path,
        string oldHash)
    {
        var manifest = new SopManifest(
            1,
            "0.0.1+old",
            DateTimeOffset.UtcNow,
            [
                new SopManifestSop(
                    sop,
                    "0.0.1+old",
                    DateTimeOffset.UtcNow,
                    [
                        new SopManifestPath(
                            path,
                            SopBundleCatalog.EmittedPathClass,
                            oldHash,
                            SopBundleCatalog.RunBacklogClaudeCommandResource),
                    ]),
            ]);
        var manifestPath = PathFor(repository, ".build/sop-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, CliJsonContext.Default.SopManifest) + Environment.NewLine);
    }

    private static IReadOnlyList<string> ManifestPaths(string repository, string sop)
    {
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(PathFor(repository, ".build/sop-manifest.json")),
            CliJsonContext.Default.SopManifest)!;
        return manifest.Sops.Single(row => row.Name == sop).Paths.Select(path => path.Path).ToList();
    }

    private static string StripCodexFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
            return content;

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.True(end >= 0, "Codex skill frontmatter must have a closing marker");
        return content[(end + "\n---\n".Length)..].TrimStart('\r', '\n');
    }

    private static void AssertThinFailClosedStub(string content, string sopName)
    {
        Assert.Contains($"build sop brief {sopName} --json", content);
        Assert.Contains("missing from PATH", content);
        Assert.Contains("is not executable", content);
        Assert.Contains("exits nonzero", content);
        Assert.Contains("unknown SOP", content);
        Assert.Contains("conductor.min_build_version", content);
        Assert.Contains("doctor failed", content);
        Assert.Contains("data.sopText", content);
        Assert.Contains("Do not use cached prose", content);
        Assert.DoesNotContain("# The ticket transaction", content);
        Assert.DoesNotContain("## Conductor", content);
        Assert.DoesNotContain("[conductor]", content);
        Assert.DoesNotContain("C:\\", content);
        Assert.DoesNotContain("/Users/", content);
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

    private static IReadOnlyList<string> ResultStatuses(JsonElement data) =>
        data.GetProperty("results")
            .EnumerateArray()
            .Select(item => item.GetProperty("status").GetString() ?? string.Empty)
            .ToList();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var target = Path.GetFileName(path) == "repo"
                    ? Directory.GetParent(path)?.FullName ?? path
                    : path;
                Directory.Delete(target, recursive: true);
            }
        }
        catch
        {
        }
    }
}

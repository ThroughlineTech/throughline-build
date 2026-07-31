using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public sealed class WorktreeLeaseManagerTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "throughline-worktree-tests", Guid.NewGuid().ToString("N"));
    private readonly string _main;
    private readonly string _root;

    public WorktreeLeaseManagerTests()
    {
        _main = Path.Combine(_temp, "repo");
        _root = Path.Combine(_temp, "leases");
        Directory.CreateDirectory(_main);
    }

    [Fact]
    public async Task SuccessfulLeaseAndTeardownRoundTripCopiesSeedsAndRunsInstall()
    {
        File.WriteAllText(Path.Combine(_main, ".dev.vars"), "SECRET=value");
        var git = new FakeGit(Sha);
        var installer = new FakeInstaller();
        var manager = CreateManager(git, installer, [".dev.vars"]);

        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", "safe-worktrees", RequiredSeed: ".dev.vars"),
            CancellationToken.None);

        Assert.True(leased.Success);
        Assert.Equal("succeeded", leased.Manifest!.Install.Status);
        Assert.Equal([".dev.vars"], leased.Manifest.SeededFiles);
        Assert.True(File.Exists(Path.Combine(leased.Manifest.WorktreePath, ".dev.vars")));
        Assert.Equal(leased.Manifest.WorktreePath, installer.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(
            leased.Manifest.WorktreePath, WorktreeLeaseConstants.ManifestFileName)));

        var removed = await manager.TeardownAsync(
            ticket: "TLB-582", directory: null, CancellationToken.None);

        Assert.True(removed.Success);
        Assert.Equal([leased.Manifest.WorktreePath], git.TrackedChangeQueries);
        Assert.Equal([leased.Manifest.WorktreePath], git.UntrackedFileQueries);
        Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Contains("lease/tlb-582-safe-worktrees", git.DeletedBranches);
        Assert.Equal([true], git.RemoveWorktreeForces);
        Assert.Equal([false], git.DeleteBranchForces);
    }

    [Fact]
    public async Task CollisionRefusalLeavesExistingLeaseUntouchedAndCreatesNothingNew()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var first = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", "first"), CancellationToken.None);
        var createCount = git.CreateCount;

        var collision = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", "second"), CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(collision.Success);
        Assert.Equal(WorktreeLeaseManager.CollisionError, collision.ErrorCode);
        Assert.Equal(createCount, git.CreateCount);
        Assert.True(Directory.Exists(first.Manifest!.WorktreePath));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task FailedCheckoutRemovesOwnedBranchButNotUnownedTarget()
    {
        var git = new FakeGit(Sha)
        {
            FailCheckout = true,
            CreateUnownedTargetOnCheckoutFailure = true
        };
        var manager = CreateManager(git, new FakeInstaller());
        var target = Path.Combine(_root, "tlb-582");
        var branch = "lease/tlb-582";

        var result = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.FailureError, result.ErrorCode);
        Assert.Equal(1, git.CreateCount);
        Assert.DoesNotContain(target, git.RemovedWorktrees);
        Assert.Contains(branch, git.DeletedBranches);
        Assert.False(git.HasBranch(branch));
        Assert.False(git.HasWorktree(target));
        Assert.True(File.Exists(Path.Combine(target, "uncommitted.txt")));
    }

    [Fact]
    public async Task SetupFailureRollsBackOnlyBranchAndWorktreeCreatedByAttempt()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new ThrowingInstaller());
        var target = Path.Combine(_root, "tlb-582");
        var branch = "lease/tlb-582";

        var result = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(target, git.RemovedWorktrees);
        Assert.Contains(branch, git.DeletedBranches);
        Assert.False(git.HasBranch(branch));
        Assert.False(git.HasWorktree(target));
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(
            target, WorktreeLeaseConstants.ManifestFileName)));
    }

    [Fact]
    public async Task ConcurrentSameTicketLoserCannotAlterWinnerOrItsUncommittedFile()
    {
        var git = new FakeGit(Sha);
        var installer = new BlockingInstaller();
        var winnerManager = CreateManager(git, installer);
        var loserManager = CreateManager(git, new FakeInstaller());

        var winnerTask = winnerManager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", "winner"), CancellationToken.None);
        await installer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var loser = await loserManager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", "loser"), CancellationToken.None);
        installer.Release.SetResult();
        var winner = await winnerTask;

        Assert.True(winner.Success);
        Assert.False(loser.Success);
        Assert.Equal(WorktreeLeaseManager.CollisionError, loser.ErrorCode);
        Assert.True(File.Exists(Path.Combine(winner.Manifest!.WorktreePath, "uncommitted.txt")));
        Assert.True(git.HasBranch(winner.Manifest.Branch));
        Assert.True(git.HasWorktree(winner.Manifest.WorktreePath));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task MissingRequiredSeedFailsBeforeCreatingWorktree()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller(), [".dev.vars"]);

        var result = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", RequiredSeed: ".dev.vars"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.MissingSeedError, result.ErrorCode);
        Assert.Equal(0, git.CreateCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "tlb-582")));
    }

    [Fact]
    public async Task TrackedSeedIsRefusedBeforeCreatingWorktree()
    {
        File.WriteAllText(Path.Combine(_main, ".npmrc"), "registry=test");
        var git = new FakeGit(Sha);
        git.TrackedPaths.Add(".npmrc");
        var manager = CreateManager(git, new FakeInstaller(), [".npmrc"]);

        var result = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("only untracked local files", result.Message);
        Assert.Equal(0, git.CreateCount);
    }

    [Fact]
    public async Task TeardownRefusesDirectoryOutsideConfiguredRoot()
    {
        var manager = CreateManager(new FakeGit(Sha), new FakeInstaller());

        var result = await manager.TeardownAsync(
            ticket: null, directory: _main, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.ContainmentError, result.ErrorCode);
    }

    [Fact]
    public async Task TeardownRejectsTamperedManifestSchema()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var manifestPath = Path.Combine(
            leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        var json = File.ReadAllText(manifestPath)
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, json);

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.InvalidManifestError, result.ErrorCode);
        Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task TeardownRejectsTamperedBranchBeforeAnyDestructiveOperation()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var manifestPath = Path.Combine(
            leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        var json = File.ReadAllText(manifestPath)
            .Replace("\"branch\": \"lease/tlb-582\"", "\"branch\": \"main\"", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, json);

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.InvalidManifestError, result.ErrorCode);
        Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Empty(git.DeletedBranches);
    }

    [Theory]
    [InlineData("baseSha")]
    [InlineData("repositoryPath")]
    [InlineData("mainWorktreePath")]
    [InlineData("worktreeRoot")]
    [InlineData("worktreePath")]
    [InlineData("ticket")]
    [InlineData("branch")]
    [InlineData("slug")]
    [InlineData("seededFiles")]
    [InlineData("leasedResources")]
    [InlineData("install")]
    [InlineData("install.status")]
    public async Task TeardownRejectsNullRequiredManifestFieldsBeforeMutation(string propertyPath)
    {
        AppContext.SetSwitch(
            "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault",
            false);
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var manifestPath = Path.Combine(
            leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        WriteTamperedManifest(manifestPath, propertyPath, writeValue: writer => writer.WriteNullValue());

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.InvalidManifestError, result.ErrorCode);
        Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task LeaseAndListSafelyRefuseNullManifestField()
    {
        AppContext.SetSwitch(
            "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault",
            false);
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var manifestPath = Path.Combine(
            leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        WriteTamperedManifest(manifestPath, "baseSha", writer => writer.WriteNullValue());

        var collisionCheck = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-583"), CancellationToken.None);
        var list = await manager.ListAsync();

        Assert.False(collisionCheck.Success);
        Assert.Equal(WorktreeLeaseManager.InvalidManifestError, collisionCheck.ErrorCode);
        Assert.Empty(list.Leases);
        Assert.Equal([leased.Manifest.WorktreePath], list.UnmanifestedDirectories);
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task TeardownRejectsMalformedNestedInstallUnderAotSerialization()
    {
        AppContext.SetSwitch(
            "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault",
            false);
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var manifestPath = Path.Combine(
            leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        WriteTamperedManifest(
            manifestPath,
            "install",
            writer =>
            {
                writer.WriteStartArray();
                writer.WriteStringValue("malformed");
                writer.WriteEndArray();
            });

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.InvalidManifestError, result.ErrorCode);
        Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task TeardownRefusesTrackedWorkBeforeRemovingAnything()
    {
        File.WriteAllText(Path.Combine(_main, ".dev.vars"), "SECRET=value");
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller(), [".dev.vars"]);
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", RequiredSeed: ".dev.vars"),
            CancellationToken.None);
        git.TrackedChanges.Add(" M tracked.txt");

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest!.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.FailureError, result.ErrorCode);
        Assert.Contains("tracked changes", result.Message);
        Assert.Equal([leased.Manifest.WorktreePath], git.TrackedChangeQueries);
        Assert.Equal([leased.Manifest.WorktreePath], git.UntrackedFileQueries);
        Assert.True(File.Exists(Path.Combine(
            leased.Manifest.WorktreePath, WorktreeLeaseConstants.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(leased.Manifest.WorktreePath, ".dev.vars")));
        Assert.True(git.HasWorktree(leased.Manifest.WorktreePath));
        Assert.True(git.HasBranch(leased.Manifest.Branch));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task TeardownRefusesUnexpectedUntrackedFilesBeforeRemovingAnything()
    {
        File.WriteAllText(Path.Combine(_main, ".dev.vars"), "SECRET=value");
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller(), [".dev.vars"]);
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582", RequiredSeed: ".dev.vars"),
            CancellationToken.None);
        git.UntrackedFiles.Add(WorktreeLeaseConstants.ManifestFileName);
        git.UntrackedFiles.Add(".dev.vars");
        git.UntrackedFiles.Add("stray.txt");

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest!.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.FailureError, result.ErrorCode);
        Assert.Contains("unexpected untracked files", result.Message);
        Assert.Equal([leased.Manifest.WorktreePath], git.TrackedChangeQueries);
        Assert.Equal([leased.Manifest.WorktreePath], git.UntrackedFileQueries);
        Assert.True(File.Exists(Path.Combine(
            leased.Manifest.WorktreePath, WorktreeLeaseConstants.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(leased.Manifest.WorktreePath, ".dev.vars")));
        Assert.True(git.HasWorktree(leased.Manifest.WorktreePath));
        Assert.True(git.HasBranch(leased.Manifest.Branch));
        Assert.Empty(git.RemovedWorktrees);
        Assert.Empty(git.DeletedBranches);
    }

    [Fact]
    public async Task TeardownForceSkipsProofAndForceDeletesBranch()
    {
        var git = new FakeGit(Sha);
        git.TrackedChanges.Add(" M tracked.txt");
        git.UntrackedFiles.Add("stray.txt");
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        var result = await manager.TeardownAsync(
            ticket: null,
            directory: leased.Manifest!.WorktreePath,
            CancellationToken.None,
            force: true);

        Assert.True(result.Success);
        Assert.Empty(git.TrackedChangeQueries);
        Assert.Empty(git.UntrackedFileQueries);
        Assert.Equal([true], git.RemoveWorktreeForces);
        Assert.Equal([true], git.DeleteBranchForces);
        Assert.False(git.HasWorktree(leased.Manifest.WorktreePath));
        Assert.False(git.HasBranch(leased.Manifest.Branch));
    }

    [Fact]
    public async Task TeardownPreservesUnmergedBranchWhenNonForceDeleteFails()
    {
        var git = new FakeGit(Sha)
        {
            FailNonForceBranchDelete = true
        };
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        var result = await manager.TeardownAsync(
            ticket: null, directory: leased.Manifest!.WorktreePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.FailureError, result.ErrorCode);
        Assert.Contains("worktree removed but branch deletion failed", result.Message);
        Assert.Equal([true], git.RemoveWorktreeForces);
        Assert.Equal([false], git.DeleteBranchForces);
        Assert.False(git.HasWorktree(leased.Manifest.WorktreePath));
        Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.True(git.HasBranch(leased.Manifest.Branch));
    }

    [Fact]
    public void WorktreeLeaseServiceReferencesNoWorkerAgentAssembly()
    {
        var references = typeof(WorktreeLeaseManager).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name.Contains("Workers", StringComparison.Ordinal) ||
            name.Contains("ModelClient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListReportsLeasesAndUnmanifestedDirectoriesWithoutMutation()
    {
        var git = new FakeGit(Sha);
        var manager = CreateManager(git, new FakeInstaller());
        var leased = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);
        var stray = Path.Combine(_root, "stray");
        Directory.CreateDirectory(stray);

        var result = await manager.ListAsync();

        Assert.Single(result.Leases);
        Assert.Equal(leased.Manifest!.Ticket, result.Leases[0].Ticket);
        Assert.Equal(leased.Manifest.Branch, result.Leases[0].Branch);
        Assert.Equal(leased.Manifest.WorktreePath, result.Leases[0].WorktreePath);
        Assert.Equal([Path.GetFullPath(stray)], result.UnmanifestedDirectories);
        Assert.Equal(1, git.CreateCount);
        Assert.Empty(git.DeletedBranches);
    }

    private WorktreeLeaseManager CreateManager(
        FakeGit git,
        FakeInstaller installer,
        IReadOnlyList<string>? seeds = null) =>
        new(
            git,
            installer,
            new WorktreeLeaseOptions(
                _main,
                _main,
                _root,
                seeds ?? Array.Empty<string>(),
                "install dependencies"));

    private static void WriteTamperedManifest(
        string path,
        string propertyPath,
        Action<Utf8JsonWriter> writeValue)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteElement(writer, document.RootElement, propertyPath.Split('.'), 0, writeValue);
        }
        File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine);
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyList<string> propertyPath,
        int depth,
        Action<Utf8JsonWriter> writeValue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(property.Name, propertyPath[depth], StringComparison.Ordinal))
                {
                    if (depth == propertyPath.Count - 1)
                        writeValue(writer);
                    else
                        WriteElement(writer, property.Value, propertyPath, depth + 1, writeValue);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            return;
        }
        element.WriteTo(writer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private class FakeInstaller : IInstallCommandRunner
    {
        public string? WorkingDirectory { get; private set; }

        public virtual Task<InstallCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken ct)
        {
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new InstallCommandResult(true, null));
        }
    }

    private sealed class ThrowingInstaller : FakeInstaller
    {
        public override Task<InstallCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken ct) =>
            throw new IOException("simulated setup failure");
    }

    private sealed class BlockingInstaller : FakeInstaller
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<InstallCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(workingDirectory, "uncommitted.txt"), "keep");
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
            return new InstallCommandResult(true, null);
        }
    }

    private sealed class FakeGit(string sha) : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = [];
        private readonly List<string> _branches = [];
        public int CreateCount { get; private set; }
        public bool FailCheckout { get; init; }
        public bool CreateUnownedTargetOnCheckoutFailure { get; init; }
        public List<string> RemovedWorktrees { get; } = [];
        public List<string> DeletedBranches { get; } = [];
        public List<string> TrackedPaths { get; } = [];
        public List<string> TrackedChanges { get; } = [];
        public List<string> UntrackedFiles { get; } = [];
        public List<string> TrackedChangeQueries { get; } = [];
        public List<string> UntrackedFileQueries { get; } = [];
        public List<bool> RemoveWorktreeForces { get; } = [];
        public List<bool> DeleteBranchForces { get; } = [];
        public bool FailNonForceBranchDelete { get; init; }

        public bool HasBranch(string branch) =>
            _branches.Contains(branch, StringComparer.Ordinal);

        public bool HasWorktree(string path) =>
            _worktrees.Any(w => string.Equals(
                w.Path, path, StringComparison.OrdinalIgnoreCase));

        public Task<string> RevParseAsync(
            string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(sha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<IReadOnlyList<string>> ListLocalBranchesAsync(
            string pattern, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _branches.Where(b => string.Equals(b, pattern, StringComparison.Ordinal)).ToList());

        public Task<IReadOnlyList<string>> FilterTrackedPathsAsync(
            IReadOnlyList<string> paths,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                paths.Where(TrackedPaths.Contains).ToList());

        public Task<GitOpResult> CreateBranchRefAsync(
            string branch,
            string fromRef,
            string worktreePath,
            CancellationToken ct)
        {
            _branches.Add(branch);
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "lease manager must create owned branch and worktree artifacts separately");

        public Task<WorktreeCreateResult> CheckoutWorktreeAsync(
            string worktreePath,
            string existingBranch,
            string mainWorktreePath,
            CancellationToken ct)
        {
            CreateCount++;
            if (FailCheckout)
            {
                if (CreateUnownedTargetOnCheckoutFailure)
                {
                    Directory.CreateDirectory(worktreePath);
                    File.WriteAllText(Path.Combine(worktreePath, "uncommitted.txt"), "keep");
                }
                return Task.FromResult(new WorktreeCreateResult(
                    false, "simulated checkout failure", null));
            }

            Directory.CreateDirectory(worktreePath);
            _worktrees.Add(new WorktreeInfo(worktreePath, existingBranch, sha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path, bool force, CancellationToken ct)
        {
            RemovedWorktrees.Add(path);
            RemoveWorktreeForces.Add(force);
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<GitOpResult> DeleteBranchAsync(
            string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            DeleteBranchForces.Add(force);
            if (FailNonForceBranchDelete && !force)
                return Task.FromResult(new GitOpResult(false, "branch is not fully merged"));
            _branches.Remove(branch);
            DeletedBranches.Add(branch);
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(
            string workingDirectory, CancellationToken ct)
        {
            TrackedChangeQueries.Add(workingDirectory);
            return Task.FromResult<IReadOnlyList<string>>(TrackedChanges.ToList());
        }

        public Task<IReadOnlyList<string>> GetUntrackedFilesAsync(
            string workingDirectory, CancellationToken ct)
        {
            UntrackedFileQueries.Add(workingDirectory);
            return Task.FromResult<IReadOnlyList<string>>(UntrackedFiles.ToList());
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(
            string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(sha);
        public Task<GitDiff> DiffAsync(
            string fromRef,
            string toRef,
            string mainWorktreePath,
            bool includePatchContent,
            CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));
        public Task<GitOpResult> FetchAsync(
            string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<RebaseResult> RebaseAsync(
            string ontoRef, string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));
        public Task<GitOpResult> RebaseAbortAsync(
            string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<GitOpResult> FastForwardMergeAsync(
            string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<int> RevListCountAsync(
            string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(
            string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}

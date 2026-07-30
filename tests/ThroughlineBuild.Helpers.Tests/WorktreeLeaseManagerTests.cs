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
        Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
        Assert.Contains("lease/tlb-582-safe-worktrees", git.DeletedBranches);
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedCreateRollsBackEveryArtifactCreatedByTheAttempt(
        bool createBranch,
        bool createWorktree)
    {
        var git = new FakeGit(Sha)
        {
            FailCreate = true,
            CreateBranchOnFailure = createBranch,
            CreateWorktreeOnFailure = createWorktree
        };
        var manager = CreateManager(git, new FakeInstaller());
        var target = Path.Combine(_root, "tlb-582");
        var branch = "lease/tlb-582";

        var result = await manager.LeaseAsync(
            new WorktreeLeaseRequest("TLB-582"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WorktreeLeaseManager.FailureError, result.ErrorCode);
        Assert.Equal(1, git.CreateCount);
        Assert.Contains(target, git.RemovedWorktrees);
        Assert.Contains(branch, git.DeletedBranches);
        Assert.False(git.HasBranch(branch));
        Assert.False(git.HasWorktree(target));
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(
            target, WorktreeLeaseConstants.ManifestFileName)));
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
        Assert.False(Directory.Exists(_root));
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

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private sealed class FakeInstaller : IInstallCommandRunner
    {
        public string? WorkingDirectory { get; private set; }

        public Task<InstallCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken ct)
        {
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new InstallCommandResult(true, null));
        }
    }

    private sealed class FakeGit(string sha) : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = [];
        private readonly List<string> _branches = [];
        public int CreateCount { get; private set; }
        public bool FailCreate { get; init; }
        public bool CreateBranchOnFailure { get; init; }
        public bool CreateWorktreeOnFailure { get; init; }
        public List<string> RemovedWorktrees { get; } = [];
        public List<string> DeletedBranches { get; } = [];
        public List<string> TrackedPaths { get; } = [];

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

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct)
        {
            CreateCount++;
            if (FailCreate)
            {
                if (CreateBranchOnFailure)
                    _branches.Add(newBranch);
                if (CreateWorktreeOnFailure)
                {
                    Directory.CreateDirectory(worktreePath);
                    _worktrees.Add(new WorktreeInfo(
                        worktreePath, newBranch, sha, false, false));
                }
                return Task.FromResult(new WorktreeCreateResult(
                    false, "simulated partial creation failure", null));
            }

            Directory.CreateDirectory(worktreePath);
            _branches.Add(newBranch);
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, sha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path, bool force, CancellationToken ct)
        {
            RemovedWorktrees.Add(path);
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<GitOpResult> DeleteBranchAsync(
            string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            _branches.Remove(branch);
            DeletedBranches.Add(branch);
            return Task.FromResult(new GitOpResult(true, null));
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

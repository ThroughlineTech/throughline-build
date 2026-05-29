using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

// Tests that redirect Console.Error via Console.SetError must not run in parallel
// with each other (Console.SetError mutates global state). Placing them in this
// shared collection forces xUnit to serialize them.
[CollectionDefinition("CommandConsoleTests")]
public sealed class CommandConsoleTestsCollection { }

// Shared fake implementations used by CloseCommandTests and DeferCommandTests.

internal sealed class TempDir : IDisposable
{
    public string Path { get; }
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tlb67-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        catch { /* best-effort cleanup */ }
    }
}

internal sealed class FakeTicketing : ITicketing
{
    private readonly Ticket _ticket;

    public List<(string id, string html)> Comments { get; } = new();
    public List<(string id, TicketState state)> Transitions { get; } = new();
    public int RollupCalls { get; private set; }
    public bool RollupThrows { get; set; }
    // Track-through count for IGitClient calls observed via the decrufter
    // (not used directly on this fake; left for symmetry).
    public int ListWorktreeCallsViaGit { get; set; }

    // Existing comments returned by GetCommentsAsync (settable by tests).
    public List<TicketComment> ExistingComments { get; set; } = new();
    public int AppendDescriptionCalls { get; private set; }
    public int ApplyLabelsCalls { get; private set; }

    public FakeTicketing(Ticket ticket) { _ticket = ticket; }

    public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

    public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(_ticket);

    public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

    public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
    {
        Transitions.Add((id, newState));
        return Task.CompletedTask;
    }

    public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
    {
        AppendDescriptionCalls++;
        return Task.CompletedTask;
    }

    public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
    {
        Comments.Add((id, html));
        return Task.FromResult($"comment-{Comments.Count}");
    }

    public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
    {
        ApplyLabelsCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

    public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct)
    {
        RollupCalls++;
        if (RollupThrows)
            throw new InvalidOperationException("simulated rollup failure");
        return Task.FromResult(new RollupResult(false, null, null));
    }

    public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<TicketComment>)ExistingComments);

    public Task<NewTicketResult> CreateTicketAsync(
        string title,
        string? type,
        string descriptionHtml,
        IReadOnlyList<string>? initialLabelNames,
        CancellationToken ct) =>
        throw new NotImplementedException();

    public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

    public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
        Task.CompletedTask;

    public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class FakeEventSink : IEventSink
{
    public List<WorkflowEvent> Events { get; } = new();

    public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
    {
        Events.Add(ev);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeGitClient : IGitClient
{
    public List<string> UnmergedBranches { get; set; } = new();
    public int ListWorktreesCalls { get; private set; }

    public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
        Task.FromResult("abc123");

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
    {
        ListWorktreesCalls++;
        return Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
    }

    public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
        Task.FromResult(new WorktreeRemoveResult(true, null));

    public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(UnmergedBranches);

    public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
        Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

    public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
        Task.FromResult("0000000000000000000000000000000000000000");

    public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
        Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

    public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) =>
        Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));

    public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(0);
    public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
}

internal sealed class FakeLlmClient : ILlmClient
{
    private readonly string _content;
    public FakeLlmClient(string content) { _content = content; }

    public Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
    {
        return Task.FromResult(new LlmResponse(_content, new LlmUsage(0, 0, null, null)));
    }

    public async IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmStreamTextDelta(_content);
        yield return new LlmStreamDone();
        await Task.CompletedTask;
    }
}

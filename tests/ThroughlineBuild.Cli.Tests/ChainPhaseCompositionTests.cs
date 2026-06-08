using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Composition-root guard for the chain verb's ChainPhase wiring. The --batch-implement feature
/// shipped broken for its whole life because the inline ChainPhase construction omitted the
/// batchWorker argument and nothing tested the composition root. ChainPhaseComposition.BuildChainPhase
/// now centralizes that wiring; this test fails if the batch worker (or the chain ship factory) is
/// ever dropped again.
/// </summary>
public class ChainPhaseCompositionTests
{
    [Fact]
    public void BuildChainPhase_WiresBatchWorker_FromImplementAgent_AndChainShipFactory()
    {
        var batchWorker = new StubWorker();
        var workerFactory = new RecordingWorkerFactory(batchWorker);
        var buildOptions = new BuildOptions(
            SessionId: "test-session",
            WorkerName: "claude-code",
            WorkerTimeout: TimeSpan.FromMinutes(5));

        // Trivial phase-factory delegates: the ChainPhase ctor only stores them, never invokes them
        // here, so null-returning lambdas are enough to exercise the wiring.
        var chain = ChainPhaseComposition.BuildChainPhase(
            new StubTicketing(),
            new StubSink(),
            buildOptions,
            planFactory: _ => null!,
            implementFactory: (_, _) => null!,
            reviewFactory: (_, _) => null!,
            shipFactory: _ => null!,
            chainShipFactory: _ => null!,
            ratifierFactory: _ => null!,
            workingDirectory: "/tmp/work",
            workerFactory,
            effectiveAgentFor: phase => $"agent-for-{phase}",
            landingRemote: "origin",
            landingPushEnabled: true);

        // The batch worker must be wired - the historical bug was exactly this argument omitted,
        // leaving the batch path in RunParentChainAsync unreachable.
        Assert.NotNull(chain.BatchWorker);
        // ...and it must be the worker resolved from the implement agent, like the per-ticket
        // implement factory uses (so batch and non-batch implement share a worker).
        Assert.Same(batchWorker, chain.BatchWorker);
        Assert.Contains("agent-for-implement", workerFactory.Requested);

        // The chain ship factory (leaf ships into the integration branch) must also be wired.
        Assert.NotNull(chain.ChainShipFactory);
    }

    private sealed class RecordingWorkerFactory : IWorkerAgentFactory
    {
        private readonly IWorkerAgent _agent;
        public List<string> Requested { get; } = new();
        public RecordingWorkerFactory(IWorkerAgent agent) { _agent = agent; }
        public IWorkerAgent Create(string agentName)
        {
            Requested.Add(agentName);
            return _agent;
        }
    }

    private sealed class StubWorker : IWorkerAgent
    {
        public string Name => "stub";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class StubSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubTicketing : ITicketing
    {
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }
}

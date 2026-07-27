using System.Runtime.CompilerServices;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
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
    public async Task SummaryJson_ActualChainCommand_SuppressesHumanStdout_AndPreservesDiagnostics()
    {
        var ticket = new Ticket(
            "TLB-1",
            "uuid-1",
            "Output routing",
            "feature",
            TicketState.Backlog,
            Size.S,
            Risk.Low,
            "<p>description</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);
        var unavailable = new TicketingUnavailableException(
            "backend down",
            new HttpRequestException("network down"));
        var ticketing = new StubTicketing(ticket, unavailable);
        var stdout = new StringWriter();
        var diagnostics = new StringWriter();
        var humanOutput = ChainPhaseComposition.SelectHumanOutput(
            summaryJson: true,
            standardOutput: stdout);
        var workerFactory = new RecordingWorkerFactory(new StubWorker());
        var buildOptions = new BuildOptions(
            SessionId: "summary-json-session",
            WorkerName: "claude-code",
            WorkerTimeout: TimeSpan.FromMinutes(5));
        var chain = ChainPhaseComposition.BuildChainPhase(
            ticketing,
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
            landingPushEnabled: true,
            output: humanOutput,
            diagnostics: diagnostics);
        var command = new ChainCommand(
            new DefaultChainRunner(chain),
            ticketing,
            humanOutput);

        var result = await command.ExecuteAsync(
            new TicketCommandContext(
                ticket.Id,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(
            $"[{ticket.Id}] chain stopped: ticketing backend unreachable - backend down{Environment.NewLine}",
            diagnostics.ToString());
    }

    [Fact]
    public void SelectHumanOutput_NormalMode_PreservesTheLiveStdoutWriter()
    {
        var stdout = new StringWriter();

        Assert.Same(
            stdout,
            ChainPhaseComposition.SelectHumanOutput(
                summaryJson: false,
                standardOutput: stdout));
        Assert.Same(
            TextWriter.Null,
            ChainPhaseComposition.SelectHumanOutput(
                summaryJson: true,
                standardOutput: stdout));
    }

    [Fact]
    public void BuildChainPhase_WiresBatchWorker_FromImplementAgent_AndChainShipFactory()
    {
        var batchWorker = new StubWorker();
        var workerFactory = new RecordingWorkerFactory(batchWorker);
        var output = new StringWriter();
        var diagnostics = new StringWriter();
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
            landingPushEnabled: true,
            output,
            diagnostics);

        // The batch worker must be wired - the historical bug was exactly this argument omitted,
        // leaving the ParentChainRunner batch path unreachable.
        Assert.NotNull(chain.BatchWorker);
        // ...and it must be the worker resolved from the implement agent, like the per-ticket
        // implement factory uses (so batch and non-batch implement share a worker).
        Assert.Same(batchWorker, chain.BatchWorker);
        Assert.Contains("agent-for-implement", workerFactory.Requested);

        // The chain ship factory (leaf ships into the integration branch) must also be wired.
        Assert.NotNull(chain.ChainShipFactory);
        Assert.Same(output, chain.OutputWriter);
        Assert.Same(diagnostics, chain.DiagnosticsWriter);

        // Omitting either dependency from a direct ChainPhase construction must be a compile-time
        // error, even though test-only callers may explicitly choose null to disable a capability.
        Assert.True(typeof(ChainPhaseExecutionDependencies)
            .GetProperty(nameof(ChainPhaseExecutionDependencies.BatchWorker))!
            .IsDefined(typeof(RequiredMemberAttribute), inherit: false));
        Assert.True(typeof(ChainPhaseFactories)
            .GetProperty(nameof(ChainPhaseFactories.ChainShip))!
            .IsDefined(typeof(RequiredMemberAttribute), inherit: false));
        Assert.True(typeof(ChainPhaseExecutionDependencies)
            .GetProperty(nameof(ChainPhaseExecutionDependencies.Output))!
            .IsDefined(typeof(RequiredMemberAttribute), inherit: false));
        Assert.True(typeof(ChainPhaseExecutionDependencies)
            .GetProperty(nameof(ChainPhaseExecutionDependencies.Diagnostics))!
            .IsDefined(typeof(RequiredMemberAttribute), inherit: false));
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
        private readonly Queue<object> _getResults;

        public StubTicketing(params object[] getResults) =>
            _getResults = new Queue<object>(getResults);

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            if (_getResults.Count == 0)
                throw new NotImplementedException();

            return _getResults.Dequeue() switch
            {
                Ticket ticket => Task.FromResult(ticket),
                Exception exception => Task.FromException<Ticket>(exception),
                var value => Task.FromException<Ticket>(
                    new InvalidOperationException($"Unsupported get result {value.GetType().Name}."))
            };
        }
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

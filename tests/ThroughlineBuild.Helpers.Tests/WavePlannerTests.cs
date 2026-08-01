using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public sealed class WavePlannerTests
{
    [Fact]
    public void DependenciesOverrideNumericOrderAndReadyPeersUseTicketNumber()
    {
        var plan = Plan(
            Ticket("TEST-10", "src/ten.cs"),
            Ticket("TEST-2", "src/two.cs"),
            new WaveTicket("TEST-1", ["src/one.cs"], ["TEST-10"]));

        Assert.Equal(["TEST-2", "TEST-10"], plan.Waves[0].Tickets);
        Assert.Equal(["TEST-1"], plan.Waves[1].Tickets);
        Assert.True(plan.Waves[0].Wave < plan.Waves[1].Wave);
    }

    [Fact]
    public void RejectsCyclesWithDedicatedException()
    {
        var error = Assert.Throws<WaveDependencyCycleException>(() => Plan(
            new WaveTicket("TEST-1", ["a"], ["TEST-2"]),
            new WaveTicket("TEST-2", ["b"], ["TEST-1"])));

        Assert.Contains("TEST-1", error.TicketIds);
        Assert.Contains("TEST-2", error.TicketIds);
    }

    [Fact]
    public void ExternalDependencyMustBeVerified()
    {
        var ticket = new WaveTicket("TEST-1", ["a"], ["TEST-99"]);

        var error = Assert.Throws<ArgumentException>(() => WavePlanner.Plan(
            [ticket], 2, Array.Empty<string>(), Array.Empty<WaveSerializeRule>()));
        Assert.Contains("unverified dependencies", error.Message);

        var plan = WavePlanner.Plan(
            [ticket], 2, ["TEST-99"], Array.Empty<WaveSerializeRule>());
        Assert.Equal(["TEST-99"], plan.VerifiedExternalDeps);
    }

    [Fact]
    public void ExactFileOverlapSerializesWithoutConfigAndNamesPath()
    {
        var plan = Plan(
            Ticket("TEST-1", "src/shared.cs"),
            Ticket("TEST-2", "src/shared.cs"));

        Assert.Equal(2, plan.Waves.Count);
        var reason = Assert.Single(Assert.Single(plan.Conflicts).Reasons);
        Assert.Equal("exact-file", reason.Rule);
        Assert.Equal("src/shared.cs", reason.Path);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void UncertainOrEmptyFilesSerializeGlobally(bool uncertain, bool hasFile)
    {
        var first = new WaveTicket(
            "TEST-1",
            hasFile ? ["src/known.cs"] : Array.Empty<string>(),
            Array.Empty<string>(),
            uncertain);
        var plan = Plan(first, Ticket("TEST-2", "docs/known.md"));

        Assert.Equal(2, plan.Waves.Count);
        Assert.Contains(
            Assert.Single(plan.Conflicts).Reasons,
            reason => reason.Rule == "uncertain");
    }

    [Fact]
    public void ConfiguredGlobalCohesiveAndPairwiseRulesNameMatchedPaths()
    {
        WaveSerializeRule[] rules =
        [
            new(WaveSerializeKind.Global, ["migrations/**"]),
            new(WaveSerializeKind.CohesiveModule, ["src/admin", "src/owner"]),
            new(WaveSerializeKind.Pairwise, ["src/contract.ts", "share-contract.md"]),
        ];
        WaveTicket[] tickets =
        [
            Ticket("TEST-1", "migrations/001.sql"),
            Ticket("TEST-2", "docs/readme.md"),
            Ticket("TEST-3", "src/admin/a.ts"),
            Ticket("TEST-4", "src/admin/b.ts"),
            Ticket("TEST-5", "src/contract.ts"),
            Ticket("TEST-6", "share-contract.md"),
        ];

        var plan = WavePlanner.Plan(tickets, 16, Array.Empty<string>(), rules);
        var reasons = plan.Conflicts.SelectMany(conflict => conflict.Reasons).ToList();

        Assert.Contains(reasons, reason =>
            reason.Rule == "global" && reason.Path == "migrations/001.sql");
        Assert.Contains(reasons, reason =>
            reason.Rule == "cohesive-module"
            && reason.Pattern == "src/admin"
            && reason.Path.Contains("src/admin/a.ts", StringComparison.Ordinal));
        Assert.Contains(reasons, reason =>
            reason.Rule == "pairwise"
            && reason.Path.Contains("src/contract.ts", StringComparison.Ordinal)
            && reason.Path.Contains("share-contract.md", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobRulesArePathAwareAndRepositoryPathsAreValidated()
    {
        Assert.True(WavePlanner.PatternMatches("*.lock", "package.lock"));
        Assert.False(WavePlanner.PatternMatches("*.lock", "nested/package.lock"));
        Assert.True(WavePlanner.PatternMatches(".github/**", ".github/workflows/ci.yml"));
        Assert.True(WavePlanner.PatternMatches("src/?dmin", "src/admin"));

        Assert.Throws<ArgumentException>(() => Plan(Ticket("TEST-1", "../outside")));
        Assert.Throws<ArgumentException>(() => Plan(Ticket("TEST-1", "C:/outside")));
        Assert.Throws<ArgumentException>(() => Plan(Ticket("TEST-1", "/outside")));
    }

    [Fact]
    public void IndependentTicketsShareWaveUpToCapAndSpeedupIsReported()
    {
        var plan = WavePlanner.Plan(
            [
                Ticket("TEST-1", "src/a.cs"),
                Ticket("TEST-2", "docs/b.md"),
                Ticket("TEST-3", "tests/c.cs"),
            ],
            2,
            Array.Empty<string>(),
            Array.Empty<WaveSerializeRule>());

        Assert.Equal(["TEST-1", "TEST-2"], plan.Waves[0].Tickets);
        Assert.Equal(["TEST-3"], plan.Waves[1].Tickets);
        Assert.Equal(1.5, plan.Speedup.EstimatedSpeedup);
        Assert.Equal("parallelism available", plan.Speedup.Verdict);
    }

    [Fact]
    public void PlannerAssemblyDoesNotReferenceWorkerAgentAssemblies()
    {
        var references = typeof(WavePlanner).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToList();

        Assert.DoesNotContain(
            references,
            name => name!.Contains("Workers", StringComparison.OrdinalIgnoreCase));
    }

    private static WavePlan Plan(params WaveTicket[] tickets) =>
        WavePlanner.Plan(
            tickets,
            8,
            Array.Empty<string>(),
            Array.Empty<WaveSerializeRule>());

    private static WaveTicket Ticket(string id, string file) =>
        new(id, [file], Array.Empty<string>());
}

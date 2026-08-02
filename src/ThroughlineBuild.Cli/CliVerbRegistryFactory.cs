using ThroughlineBuild.Commands;

namespace ThroughlineBuild.Cli;

public static class CliVerbRegistryFactory
{
    private static readonly (string Name, CliVerbKind Kind, bool RunsBeforeConfig)[] Verbs =
    [
        ("init", CliVerbKind.Init, true),
        ("settarget", CliVerbKind.SetTarget, true),
        ("user-guide", CliVerbKind.UserGuide, true),
        ("op-doc", CliVerbKind.OpDoc, true),
        ("models", CliVerbKind.Models, true),
        ("sop", CliVerbKind.Sop, true),
        ("sweep", CliVerbKind.Sweep, false),
        ("candidate", CliVerbKind.Candidate, false),
        ("worktree", CliVerbKind.Worktree, false),
        ("gate", CliVerbKind.Gate, false),
        ("waves", CliVerbKind.Waves, false),
        ("list", CliVerbKind.List, false),
        ("get", CliVerbKind.Get, false),
        ("comments", CliVerbKind.Comments, false),
        ("comment", CliVerbKind.Comment, false),
        ("transition", CliVerbKind.Transition, false),
        ("relate", CliVerbKind.Relate, false),
        ("setup", CliVerbKind.Setup, false),
        ("amend", CliVerbKind.Amend, false),
        ("close", CliVerbKind.Close, false),
        ("defer", CliVerbKind.Defer, false),
        ("reopen", CliVerbKind.Reopen, false),
        ("new", CliVerbKind.New, false),
        ("scaffold", CliVerbKind.Scaffold, false),
        ("rework", CliVerbKind.Rework, false),
        ("decompose", CliVerbKind.Decompose, false),
        ("plan", CliVerbKind.Plan, false),
        ("implement", CliVerbKind.Implement, false),
        ("review", CliVerbKind.Review, false),
        ("ship", CliVerbKind.Ship, false),
        ("chain", CliVerbKind.Chain, false),
    ];

    public static CliVerbRegistry Build()
    {
        var registry = new CliVerbRegistry();
        foreach (var verb in Verbs)
            registry.Register(new CliVerb(verb.Name, verb.Kind, verb.RunsBeforeConfig));
        return registry;
    }
}

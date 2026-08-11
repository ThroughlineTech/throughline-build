namespace ThroughlineBuild.Commands;

/// <summary>
/// Describes a top-level CLI verb and the bootstrap stage in which it runs.
/// The CLI owns composition; this contract owns dispatch identity and ordering.
/// </summary>
public interface ICliVerb
{
    string Name { get; }
    CliVerbKind Kind { get; }
    bool RunsBeforeConfig { get; }
}

public enum CliVerbKind
{
    Init,
    Install,
    SetTarget,
    UserGuide,
    OpDoc,
    Models,
    Sop,
    Conductor,
    Profile,
    Sweep,
    Candidate,
    Worker,
    Worktree,
    Gate,
    Waves,
    List,
    Get,
    Comments,
    Comment,
    Attachments,
    Attachment,
    Evidence,
    Transition,
    Relate,
    Setup,
    Amend,
    Close,
    Defer,
    Reopen,
    New,
    Scaffold,
    Rework,
    Decompose,
    Plan,
    Implement,
    Review,
    Ship,
    Chain,
}

public sealed record CliVerb(string Name, CliVerbKind Kind, bool RunsBeforeConfig) : ICliVerb;

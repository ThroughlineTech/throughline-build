namespace ThroughlineBuild.Phases;

/// <summary>
/// Options passed to DraftPhase.RunAsync to control the draft operation.
/// </summary>
/// <param name="OperatorText">Free-form operator input describing the desired ticket.</param>
/// <param name="Debug">When true, the worker captures debug artefacts to a temp directory.</param>
public sealed record DraftPhaseOptions(
    string OperatorText,
    bool Debug);

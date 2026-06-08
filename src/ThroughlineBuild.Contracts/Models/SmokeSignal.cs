namespace ThroughlineBuild.Contracts.Models;

public enum SmokeSignalKind { DiffFacts, GrepPresent, GrepAbsent }

// Advisory, non-blocking observation over the diff or worktree. Never gate-failing.
// Two producers share this shape: SmokeCollector (diff facts + grep) and advisory
// check results from AutomatedChecksRunner (lint, format). The Kind field is the
// structural discriminator from CheckResult; Details is consumable by the LLM verifier
// as a prior rather than a hard verdict.
public record SmokeSignal(
    string Label,
    SmokeSignalKind Kind,
    // Predicate satisfied: GrepPresent=found; GrepAbsent=absent (clean); DiffFacts=always true.
    bool Matched,
    string Details);

namespace ThroughlineBuild.Contracts.Models;

// Verifier kinds for ac_bindings.
// Hard-gating: test, grep-present, grep-absent, file, exit, golden.
// Smoke-signal (advisory, non-blocking): smoke.
public enum VerifierKind { Test, GrepPresent, GrepAbsent, File, Exit, Golden, Smoke }

// One acceptance-criteria binding: a reference to an AC item plus how it is verified.
public record AcBinding(
    string AcRef,        // AC identifier, e.g. "AC-1" or a short label
    VerifierKind Kind);

// Artifact returned by the implement worker and validated by the gate.
// Hook fields (RedGreenKind, Tier, RoutingKey) are forward-compatible placeholders
// for deferred tier-routing work. UNENFORCED: every consumer in this operation
// ignores these fields. Do not add gate logic against them until a future brief
// explicitly activates them.
public record CompletionClaim(
    // What this implementation delivers (output artifact paths or module names).
    IReadOnlyList<string> Provides,
    // What this implementation depends on (input artifact paths or module names).
    IReadOnlyList<string> Consumes,
    // Bindings from acceptance-criteria items to verifier kinds.
    IReadOnlyList<AcBinding> AcBindings,
    // Net-new test names or test file paths added in this implementation.
    IReadOnlyList<string> TestsAdded,

    // --- deferred-tier hook fields: present but UNENFORCED ---
    // Reserved for a future red/green verifier-kind gate tier. Ignored by all consumers.
    VerifierKind? RedGreenKind = null,
    // Reserved for a future tier-routing slot (e.g. "smoke", "regression"). Ignored.
    string? Tier = null,
    // Reserved for a future per-class routing key. Ignored.
    string? RoutingKey = null);

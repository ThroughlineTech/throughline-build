# RTK Integration Analysis

## Executive Summary

RTK fits Latticeflow well, but it should integrate inside each spawned agent's command-execution path, not around the `claude` or `codex` processes themselves.

Wrapping an agent invocation as `rtk claude` or `rtk codex` would not provide the intended benefit. RTK needs to intercept commands that the agent subsequently runs, such as `git`, `dotnet test`, `rg`, and file-reading commands.

The recommended initial integration is:

- Claude Code only.
- Transparent `PreToolUse` command rewriting.
- A Latticeflow-controlled `--rtk` / `--no-rtk` flag.
- No changes to worker prompts.
- Session-level metadata and paired A/B measurement.

## Current Architecture Fit

Latticeflow already contains most of the infrastructure needed for a credible RTK experiment:

- Workers receive briefs over stdin, preserving prompt identity.
- `WorkerOptions` supports environment-variable injection.
- Claude debug transcripts capture turns, tool-result sizes, usage, cache reads, timing, and files accessed.
- Debug transcripts include a prompt SHA-256, allowing experiments to prove that both runs received identical worker briefs.
- Event logs already record worker activity and LLM usage metrics.

Relevant implementation points include:

- `src/ThroughlineBuild.Workers.Codex/CodexAgent.cs`
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs`
- `src/ThroughlineBuild.Contracts/IWorkerAgent.cs`
- `src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs`
- `docs/event-log-format.md`
- `docs/debug-transcript-format.md`

This foundation makes it possible to measure RTK as an isolated runtime treatment rather than relying on subjective impressions.

## Relevant RTK Capabilities

RTK provides:

- Command rewriting through `rtk rewrite`.
- Claude `PreToolUse` interception.
- Compact output for git, file discovery, tests, builds, package tools, and other commands.
- Full raw failed output through its tee mechanism.
- Per-command input/output estimates and savings tracking.
- Project-scoped gain reporting.
- Pass-through behavior for unsupported commands.

The current RTK source uses the native hook command:

```text
rtk hook claude
```

This is preferable to the older shell-script hook. Some RTK documentation still describes native Windows as requiring instruction fallback, while the current source contains a native binary hook. Latticeflow should therefore pin and verify a minimum supported RTK version rather than infer behavior from older documentation.

## Proposed Configuration

Add a global experiment section to `.build/config.toml`:

```toml
[experiments.rtk]
enabled = false
executable = "rtk"
fail_open = true
telemetry = false
tracking = true
agents = ["claude-code"]
```

Add CLI overrides:

```text
build implement TLB-123 --rtk
build implement TLB-123 --no-rtk
build chain TLB-123 --rtk
```

Configuration precedence should be:

```text
CLI flag > environment variable > config default
```

For CI and scripted runs, support:

```text
BUILDFLOW_RTK=on|off
```

Event data should record the resolved setting, not merely the configured default.

## Implementation Shape

Introduce a typed worker launch policy instead of placing RTK conditionals throughout the phases:

```csharp
enum CommandOutputMode
{
    Native,
    Rtk
}

record WorkerLaunchProfile(
    CommandOutputMode CommandOutputMode,
    string? ExperimentId);
```

Pass this policy through `WorkerAgentBuilder` into each agent adapter. Workflow phases should remain unaware of RTK.

For Claude Code, the worker adapter should:

1. Verify `rtk --version`.
2. Enable the `rtk hook claude` `PreToolUse` hook.
3. Set `RTK_TELEMETRY_DISABLED=1`.
4. Set a Latticeflow run identifier.
5. Spawn Claude normally.
6. Fail open if RTK is unavailable, unless strict mode was explicitly requested.

Latticeflow should not modify the user's global `~/.claude/settings.json` during every run. Prefer a generated session settings overlay or an RTK-supported environment toggle.

## Recommended RTK Extensions

RTK currently lacks a clean process-wide experiment switch. Its documented `RTK_DISABLED=1` behavior is oriented toward command prefixes rather than disabling all hook activity for a spawned agent process.

The following RTK environment variables would make integration substantially cleaner:

```text
RTK_DISABLED=1
RTK_RUN_ID=<latticeflow-session-id>
RTK_AUDIT_DIR=<session-artifact-directory>
RTK_TRACKING_DB=<session-specific-database>
```

The hook should immediately pass through when `RTK_DISABLED=1` is present.

`RTK_RUN_ID` should be stored on tracking records so commands can be attributed to an exact Latticeflow worker. This is especially important because Latticeflow supports parallel workers.

A session-specific tracking database would prevent concurrent runs from being mixed in RTK's global SQLite history. RTK already enables SQLite WAL mode, but WAL only addresses concurrent access; it does not provide reliable experiment attribution.

## Claude Code and Codex Differences

### Claude Code

Claude Code provides the cleanest experiment:

- The hook transparently rewrites shell commands.
- The worker brief remains byte-for-byte identical.
- The model does not need RTK instructions.
- Enabled and disabled runs differ primarily in the command output returned to the model.

### Codex

RTK's current Codex integration injects awareness through `AGENTS.md`. Enabling it therefore changes the model's instructions and potentially its command-selection behavior.

That would test "RTK plus instruction prompting," not command-output compression alone.

Recommended rollout:

1. Ship transparent Claude Code support first.
2. Label Codex support experimental.
3. Exclude Codex from the headline A/B results.
4. Add Codex when a programmatic command hook is available, or build a dedicated integration that does not modify repository instructions.

## Measurement Plan

### Efficiency Metrics

Measure:

- Input tokens.
- Cache-read tokens.
- Output tokens.
- Number of turns.
- Tool-call count.
- Tool-result bytes and lines.
- Worker wall-clock time.
- Cost, where exposed by the provider.

### Quality Metrics

Measure:

- Worker completion status.
- First-review pass rate.
- Rework rounds.
- Gate failures.
- Worker-result envelope parse failures.
- Final diff correctness.
- Ticket outcome.
- Unnecessary file reads.
- Raw-output fallback frequency.

RTK's estimated token savings should be treated as supporting evidence. Latticeflow's provider-reported usage should remain the authoritative measurement.

## A/B Methodology

Use paired runs from identical git commits and ticket snapshots. Alternate treatment order to reduce timing and model-service bias:

```text
A: RTK off
B: RTK on
B: RTK on
A: RTK off
```

Every pair should use:

- The same model and reasoning effort.
- The same prompt SHA-256.
- The same worker permissions.
- Fresh, equivalent worktrees.
- The same ticket content.
- Debug capture enabled.
- No reuse of modified working state.

Run at least 20 paired tasks per agent, model, and phase. Planning, implementation, and review results should be analyzed separately because their command-output profiles differ significantly.

## Expected Results

The largest gains should appear in:

- Repository investigation.
- Large git diffs and logs.
- Test output.
- Build failures.
- Repeated `rg`, directory, and file inspection.
- Review workers reading broad code surfaces.

The primary savings mechanism should be smaller tool results entering the model context. This can reduce both the immediate input size and the cache-read cost of later turns.

Command filtering is unlikely to reduce final-answer output tokens dramatically. Quality may improve because the model sees less irrelevant output, but aggressive filtering may also hide relevant warnings or test details. RTK's raw-output tee is therefore an important safety mechanism.

## Proposed Event Metadata

Add the following fields to worker-spawn or experiment events:

```json
{
  "command_output_mode": "rtk",
  "rtk_requested": true,
  "rtk_active": true,
  "rtk_version": "0.x.y",
  "rtk_fail_open": true,
  "experiment_id": "rtk-2026-06-12-001",
  "comparison_group": "TLB-123-implement",
  "comparison_arm": "B"
}
```

Requested and active values must be separate. A run where `--rtk` was requested but RTK could not be started must not be reported as an RTK treatment run.

Useful post-run fields include:

```json
{
  "rtk_rewrite_count": 18,
  "rtk_passthrough_count": 7,
  "rtk_raw_output_tokens_estimated": 24000,
  "rtk_filtered_output_tokens_estimated": 6200,
  "rtk_saved_tokens_estimated": 17800,
  "rtk_fallback_count": 1
}
```

## Failure Behavior

Default behavior should be fail-open:

- Missing RTK binary: warn, record `rtk_active=false`, and run the worker normally.
- Unsupported RTK version: warn and run normally.
- Hook initialization failure: warn and run normally.
- RTK command parse failure: RTK should pass through or expose raw output.
- Tracking failure: continue the worker without tracking.

An optional strict mode can be added for controlled experiments:

```text
build implement TLB-123 --rtk --rtk-required
```

In strict mode, failure to activate RTK should stop before the worker is spawned. This prevents accidental contamination of a benchmark arm.

## Delivery Sequence

1. Add resolved RTK mode and experiment ID to worker and event metadata.
2. Extend RTK with process-wide disable and run attribution.
3. Implement Claude Code launch-policy support.
4. Add `--rtk` and `--no-rtk` CLI flags.
5. Extend session artifacts with rewrite counts and RTK gain metrics.
6. Build an A/B report over existing event and transcript data.
7. Run Claude Code trials.
8. Evaluate Codex separately.

## Conclusion

RTK should remain an observable worker-runtime treatment rather than becoming part of Latticeflow's prompts or workflow semantics.

Keeping the integration at the worker launch boundary makes it:

- Reversible.
- Measurable.
- Agent-specific.
- Independent of workflow phases.
- Compatible with deterministic prompt comparison.

Claude Code's transparent hook provides the best first implementation. Codex should follow only when its integration can avoid changing the worker's instruction context, or when that instruction change is explicitly treated as a separate experiment.

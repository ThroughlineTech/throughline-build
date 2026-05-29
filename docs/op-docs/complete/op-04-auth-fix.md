# Operation: build-auth-path-fix

Correct the auth-path mismatch that prevents the plan phase from doing an apples-to-apples cost comparison against the old `/ti` slash command. The CLI currently requires `ANTHROPIC_API_KEY` at startup even though `AnthropicClient` is never used by the plan phase, and the subprocess inherits the env var, forcing Claude Code into API-key billing instead of the OAuth/subscription path the old system uses.

## Why this exists

Op-doc 3 wired `AnthropicClient` into the design but never connected it to the plan phase logic (the plan phase delegates all LLM work to the `ClaudeCodeAgent` subprocess). The CLI's `ResolveSecrets` unconditionally requires `ANTHROPIC_API_KEY`, then stores the value without using it (state report sections 2 and 6). Worse, because the subprocess inherits the parent's environment, Claude Code sees `ANTHROPIC_API_KEY` and switches from its default OAuth credential path to API-key auth (state report section 8). Concrete consequences:

- The cost comparison vs the old `/ti` becomes invalid: old runs on subscription billing (effectively free at the margin), new runs on API billing (per-token charges)
- A user without an Anthropic API key cannot run the CLI at all, even when their `claude login` OAuth session is fully valid (confirmed in the state report: `claude --version` succeeds without `ANTHROPIC_API_KEY`)
- The promised cost reduction cannot be measured cleanly until both systems use the same billing path

This op-doc strips the dead requirement and ensures the subprocess gets a clean environment. Architectural use of `AnthropicClient` for future judgment slots is preserved; it just becomes opt-in at the point of need rather than mandatory at startup.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Auth-path correction | - | S |

## Plan A: Auth-path correction

### Goal

The CLI runs end-to-end without `ANTHROPIC_API_KEY` set in the environment. When `ClaudeCodeAgent` spawns its subprocess, the subprocess does NOT see `ANTHROPIC_API_KEY` even if the parent process has it set. This makes the plan-phase LLM work use Claude Code's OAuth/subscription auth, matching the old `/ti` path for apples-to-apples cost comparison.

Brief sequence: B01 makes secret resolution lazy so missing `ANTHROPIC_API_KEY` no longer blocks startup. B02 strips `ANTHROPIC_API_KEY` from the worker subprocess env so even when present at startup it does not reach Claude Code. B03 adds an integration test that proves the CLI runs without the env var.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | lazy-secret-resolution | Make ANTHROPIC_API_KEY resolution lazy; do not require it at startup | - | src/ThroughlineBuild.Cli/Config.cs, src/ThroughlineBuild.Cli/Program.cs |
| 02 | worker-env-clean | ClaudeCodeAgent explicitly removes ANTHROPIC_API_KEY from the subprocess env | - | src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs |
| 03 | run-without-key | Integration test confirms `build plan` runs without ANTHROPIC_API_KEY set | 01, 02 | tests/ThroughlineBuild.Cli.Tests/AuthPathTests.cs |

### Briefs - detail

#### Brief 01: lazy-secret-resolution

Goal: Make secret resolution lazy and per-client. `ANTHROPIC_API_KEY` is required only when `AnthropicClient` is actually instantiated (which is never, for the plan phase). `PLANE_API_TOKEN` is still required at startup because `PlaneTicketingClient` is always instantiated for the plan phase.

Inputs:
- Current `Config.cs` and `Program.cs` from the throughline-build repo
- The state report confirming `ANTHROPIC_API_KEY` is fetched at startup (Config.cs:89-93) but never referenced afterward

Outputs:
- `BuildConfigLoader.ResolveSecrets` no longer requires `ANTHROPIC_API_KEY` unconditionally
- A small factory or accessor pattern lets clients that need a specific secret fail loudly when constructed without it, rather than pre-validating every possible secret at startup
- `PLANE_API_TOKEN` requirement is preserved (still checked at startup since `PlaneTicketingClient` is always wired)
- Existing CLI error paths still exit with appropriate codes when truly required secrets are missing

Acceptance:
- [ ] `build plan <id>` runs without `ANTHROPIC_API_KEY` set in the environment
- [ ] `build plan <id>` still fails fast with exit code 3 when `PLANE_API_TOKEN` is missing, with a clear error message identifying the missing variable
- [ ] When a future code path needs `ANTHROPIC_API_KEY` (e.g., direct AnthropicClient usage for a judgment slot), that code path fails loudly at the point of need, not at startup, with a message identifying which feature requires the variable
- [ ] Existing xUnit tests pass; a new test covers the "no anthropic key" startup path

Notes: Multiple acceptable shapes. Simplest: gate the existing check on whether any code path in the active phase needs the secret (for v1 plan-only CLI, the gate is always "no"). Cleaner: introduce a small `ISecretProvider` interface that clients depend on; `PlaneTicketingClient` takes one for the Plane token; `AnthropicClient` (when wired in a later op-doc) takes one for the Anthropic key. The provider reads env vars on demand and throws if absent. Pick the smaller of the two unless the cleaner shape is roughly the same effort.

OOS:
- Do not implement a secret manager service or cloud-secrets integration
- Do not add config for opt-in API-key billing for the worker (separate concern, future op-doc)
- Do not refactor `PlaneTicketingClient`'s constructor signature beyond what is necessary
- Do not preemptively wire `AnthropicClient` into the plan phase

#### Brief 02: worker-env-clean

Goal: When `ClaudeCodeAgent` spawns the Claude Code subprocess, the subprocess environment must NOT include `ANTHROPIC_API_KEY`, regardless of whether the parent process has it set. This ensures Claude Code falls back to its OAuth credential path, matching the old `/ti` slash command's billing model.

Inputs:
- Current `ClaudeCodeAgent.ExecuteAsync` from the state report (ClaudeCodeAgent.cs:17-84)
- Knowledge of `ProcessStartInfo.Environment` semantics in .NET: when `UseShellExecute = false`, the subprocess inherits the parent's environment by default; mutations to `psi.Environment` overlay on the inherited set
- Knowledge of `IDictionary<string, string?>.Remove(key)` for the env override map

Outputs:
- Updated `ClaudeCodeAgent.cs` that, after constructing `ProcessStartInfo` and applying any `options.EnvironmentVariables` overrides, explicitly removes `ANTHROPIC_API_KEY` from `psi.Environment` before starting the process
- The removal is unconditional in v1 (matches the apples-to-apples requirement)
- One code comment explains the rationale: ensure Claude Code uses OAuth auth rather than API-key auth so worker LLM cost flows to the user's subscription, not to per-token API billing. The comment does NOT reference the old system or claude-config.
- Existing xUnit fixture-process tests still pass

Acceptance:
- [ ] After `psi.Environment` mutations from `options.EnvironmentVariables` (if any) are applied, `psi.Environment.Remove("ANTHROPIC_API_KEY")` (or equivalent) is called before `Process.Start`
- [ ] The removal happens regardless of whether `options.EnvironmentVariables` is null or provides explicit env overrides
- [ ] If `options.EnvironmentVariables` explicitly sets `ANTHROPIC_API_KEY` to a value (a deliberate opt-in for API-key mode), that value wins (caller intent respected via override applied after the removal). If null or not present in the override map, the env var is absent from the subprocess.
- [ ] Code comment present explaining the auth-path rationale, written in technical terms only
- [ ] xUnit test covers: env var present in parent, subprocess env confirmed absent; env var present in parent and explicit override in `WorkerOptions`, subprocess env confirmed has the override value

Notes: `psi.Environment` is the typed `IDictionary<string, string?>` for environment overrides on `ProcessStartInfo`. `.Remove(key)` is the standard call. The order of operations matters: apply override map first, then remove `ANTHROPIC_API_KEY` if not in the override map. The simplest formulation: do the removal, then re-apply the override map (so any explicit setting overrides the removal). Either order produces the same final state; pick the one that reads more clearly.

OOS:
- Do not add an "auth mode" config option (future op-doc, once a judgment-slot phase needs the API key)
- Do not modify other env vars in the subprocess
- Do not introduce conditional logic based on the value of `ANTHROPIC_API_KEY` (presence/absence handling is enough)
- Do not strip `ANTHROPIC_API_KEY` from the parent process's own environment

#### Brief 03: run-without-key

Goal: An integration test that proves the CLI runs `build plan` end-to-end without `ANTHROPIC_API_KEY` set in the environment. This is the regression gate that prevents this issue from recurring.

Inputs:
- The completed Brief 01 and Brief 02 changes
- xUnit test infrastructure from earlier briefs
- A way to launch the CLI from a test (invoke `Program.Main` directly with mocked dependencies, or `Process.Start` against the built binary)

Outputs:
- `tests/ThroughlineBuild.Cli.Tests/AuthPathTests.cs` with at least two tests:
  - `Cli_runs_plan_without_anthropic_key`: invokes the CLI with `ANTHROPIC_API_KEY` unset, verifies the run completes the startup and config-resolution phases without exit code 3
  - `Cli_fails_when_plane_token_missing`: invokes the CLI with `PLANE_API_TOKEN` unset, verifies exit code 3 and the message references `PLANE_API_TOKEN`

Acceptance:
- [ ] Both tests pass
- [ ] Tests do not require real Plane or a real Claude Code subprocess to run (mock or stub the DI seam as needed)
- [ ] Tests run in CI on all three OS matrix jobs
- [ ] Failure of either test fails the CI job

Notes: For the "runs plan without anthropic key" test, mocking `PlaneTicketingClient` and `ClaudeCodeAgent` at the DI seam is the cleanest approach. Run `build plan <fake-id>` against the mocks and verify the process reaches the expected log/event before any external call. If DI mocking is hard given current code shape, an alternative is to verify the startup phase completes (config loads, secrets resolve, dispatch begins) without exit code 3, even if the subsequent Plane call fails for unrelated reasons.

OOS:
- Do not test against the real Anthropic API or a real Claude Code subprocess in this brief
- Do not test the cost comparison itself (that is the dogfooding run, not a unit test)
- Do not test other phases (only plan exists today)

## What done looks like

After this op-doc lands, `build plan <id>` runs successfully when invoked from a terminal that has no `ANTHROPIC_API_KEY` set, provided `PLANE_API_TOKEN` is set and the user has a valid `claude login` OAuth session on the machine. When the Claude Code subprocess is spawned, its environment does not contain `ANTHROPIC_API_KEY`, so Claude Code uses OAuth auth and bills against the user's Claude subscription. The cost comparison against the old `/ti` slash command is now apples-to-apples: both paths route LLM work through Claude Code on the same billing channel.

Three follow-ups surfaced by the state report, worth tracking but not in scope here:

- **WORKER_RESULT format spec divergence.** Op-doc 3 Brief 03 specified a fenced JSON block (```json WORKER_RESULT). The implementation uses a bare `WORKER_RESULT` marker line followed by JSON on the next non-empty line. Both work; spec and implementation should converge. Recommendation: update the spec to match the implementation (the bare form is terser and the parser is already built around it). Doc change only, no code change.
- **Label preservation gap.** Op-doc 3 Brief 02 step 14 specified "read current labels first, then `ApplyLabelsAsync` with the union." The implementation calls `ApplyLabelsAsync(ticketId, new[] { $"risk:{riskLabel}", $"size:{sizeLabel}" }, ct)` directly, which clobbers existing labels because `PlaneTicketingClient.ApplyLabelsAsync` replaces. Worth fixing before this CLI handles tickets that already carry labels you care about.
- **`WorkerResultParser` robustness.** If stdout contains an accidental `WORKER_RESULT` line before the real one, the parser bails on the first invalid JSON without continuing to scan. Low-priority but a one-line fix when convenient (continue the outer loop instead of returning null).

These three are good candidates for the first real tickets handled by the new system itself, once the cost comparison validates the architecture.
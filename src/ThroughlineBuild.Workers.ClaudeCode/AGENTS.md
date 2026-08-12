# ThroughlineBuild.Workers.ClaudeCode - Claude worker implementation

`ClaudeCodeAgent : IWorkerAgent` selects transport. Config omission and the
generated template default to `interactive-hook`; `print` is the explicit
rollback, and this repository's tracked config currently selects it.

Interactive runs use ConPTY plus mandatory job object on Windows and PTY plus
process-group containment on Unix. Completion is synthesized from the persisted
Claude transcript; the correlated Stop hook is best effort only.
`ClaudeCodePreflight` requires Claude CLI 2.1.177+ and never silently falls back
to print. Trust, run-directory, and worktree-lock state live outside the repo
except gitignored `.build/brief.md`.

Keep parsing AOT-safe and route final output through shared `WorkerResultParser`.
The sibling `ThroughlineBuild.ClaudeCode` facade owns public API; this project
owns transport internals.

Agent-layer detail:
[../../docs/state-of-the-system/11-llm-architecture.md](../../docs/state-of-the-system/11-llm-architecture.md).

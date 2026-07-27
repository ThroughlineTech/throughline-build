# ThroughlineBuild.Workers.ClaudeCode - Claude worker implementation

`ClaudeCodeAgent : IWorkerAgent` selects one of two transports. Product config
defaults to `interactive-hook`; `print` is the explicit rollback.

Interactive runs use ConPTY plus a mandatory job object on Windows and a PTY
plus process-group containment on Unix. Completion is synthesized from the
persisted Claude transcript; the correlated Stop hook is best effort only.
`ClaudeCodePreflight` requires Claude CLI 2.1.177 or newer and never silently
falls back to print. Trust, run-directory, and worktree-lock state is outside
the repository except for the gitignored `.build/brief.md`.

Keep parsing AOT-safe and route final output through the shared
`WorkerResultParser`. The reusable public API lives in the sibling
`ThroughlineBuild.ClaudeCode` facade; this project owns transport internals.

Agent-layer detail:
[../../docs/state-of-the-system/11-llm-architecture.md](../../docs/state-of-the-system/11-llm-architecture.md).

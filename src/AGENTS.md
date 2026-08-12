# src/ - library + CLI orientation

20 projects (19 libraries + Cli) compile into the Native AOT `build` binary.
Architecture overview:
[docs/state-of-the-system/00-index.md](../docs/state-of-the-system/00-index.md).

Dependency order (leaf -> root):
`Contracts`, `ModelClient` -> `Git`, `Helpers`, `EventLog`, `Plane`,
`JudgmentSlots`, `Scaffold`, `Workers.Common` -> `Briefs`, `Anthropic`,
`Workers.{ClaudeCode,Codex,Gemini,Copilot}` -> `ThroughlineBuild.ClaudeCode`
(public facade), `Verification` -> `Phases` -> `Commands` -> `Cli`.

Root `AGENTS.md` owns the AOT, source-generated JSON, Contracts I/O-free, and
solution-file rules. Local reminder: only projects in `throughline-build.sln`
are real; untracked source directories are debris.

Trust code over docs. The state-of-the-system set describes the current tree.

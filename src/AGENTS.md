# src/ - library + CLI orientation

19 projects (1 CLI entry point + 18 libraries) compiled AOT into the `build`
binary. Full per-project index with status tags and gotchas: [docs/state-of-the-system/01-inventory.md](../docs/state-of-the-system/01-inventory.md).
Architecture overview: [docs/state-of-the-system/00-index.md](../docs/state-of-the-system/00-index.md).

Dependency order (leaf -> root):
`Contracts` -> `ModelClient`, `Git`, `Helpers`, `EventLog`, `Plane`, `Briefs`,
`JudgmentSlots` -> `Anthropic`, `Workers.Common`, `Verification` ->
`Workers.{ClaudeCode,Codex,Gemini,Copilot}`, `Scaffold`, `Phases` ->
`Commands` -> `Cli`.

AOT discipline: `Cli` sets `PublishAot=true`. Use source-generated
`JsonSerializerContext` for anything serialized; do not rely on reflection-based
serialization. Keep `Contracts` I/O-free.

Trust the code over the docs: state-of-the-system was written at an older commit
and flags its own drift. Where a doc disagrees with HEAD, the code wins.

# Briefs/Templates - per-agent brief templates

Per-agent directories (`claude-code/`, `codex/`, `copilot/`, `gemini/`) hold
`plan.md`, `implement.md`, `review.md`, `draft.md`, `decompose.md`,
`batch-implement.md`, and `batch-review.md`. `shared/` fragments are loaded by
`TemplateLoader.LoadShared`; parent builders fill `{{placeholders}}`, including
`{{preloaded_context_section}}`.

GOTCHA - embedded resources: the csproj embeds `Templates\**\*.md`; rebuild
after edits. New files are picked up automatically.

GOTCHA - git-state bans (op-29): `implement.md` forbids `git stash` because the
stash stack is repo-global; `review.md` is git read-only with no
stash/checkout/reset/rebase. Keep these bans.

GOTCHA - line endings: `.gitattributes` pins `Templates/**/*.md` to `eol=lf`.
Snapshot tests compare exact bytes. Edit as LF, run Briefs.Tests, and update
`Snapshots/` only for intended diffs.

# Briefs/Templates - per-agent brief templates

Per-agent subdirectories (`claude-code/`, `codex/`, `copilot/`, `gemini/`)
each hold 7 templates: `plan.md`, `implement.md`, `review.md`, `draft.md`,
`decompose.md`, `batch-implement.md`, `batch-review.md`. `shared/` holds
agent-agnostic fragments (WORKER_RESULT sections, obsolete-detection blocks,
patch-fetch directives) loaded via `TemplateLoader.LoadShared`. The builders
in the parent project (`PlanBriefBuilder`, `BatchImplementBriefBuilder`, ...)
fill `{{placeholders}}` (incl. `{{preloaded_context_section}}`).

GOTCHA - embedded resources: the csproj globs `Templates\**\*.md` as
EmbeddedResource; edits need a rebuild to take effect, new files are picked up
automatically.

GOTCHA - git-state bans (op-29): `implement.md` forbids `git stash` (stash
stack is repo-global, leaks across worktrees); `review.md` is read-only with
respect to git - no stash/checkout/reset/rebase. Keep these when editing.

GOTCHA - line endings: `.gitattributes` pins `Templates/**/*.md` to `eol=lf`.
Snapshot tests in `tests/ThroughlineBuild.Briefs.Tests` compare exact bytes,
so a CRLF edit on Windows breaks snapshots. Edit as LF, then run Briefs.Tests
and update `Snapshots/` if the diff is intended.

# Briefs/Templates - per-agent brief templates

Per-agent subdirectories: `claude-code/`, `codex/`, `copilot/`, `gemini/`, plus
a `shared/` dir of cross-agent fragments (batch-implement / batch-review worker-
result envelopes, obsolete-rework guidance, patch-fetch directives). Each agent
dir holds `plan.md`, `implement.md`, `review.md`, `draft.md`, `decompose.md`,
`batch-implement.md`, `batch-review.md`. The per-phase builders in the parent
project (`PlanBriefBuilder`, `ImplementBriefBuilder`, etc.) load these via
`TemplateLoader` and fill in project context.

GOTCHA - the completion-claim contract (op-30): every `implement.md` instructs
the worker to emit a `COMPLETION_CLAIM` fenced block + a `completion_claim_ref`
metadata key; the gate parses it (`CompletionClaimParser`). Do not drop that
block when editing implement templates.

GOTCHA - git-state bans (op-29): `implement.md` forbids `git stash` (the stash
stack is repo-global and leaks across worktrees); `review.md` is read-only with
respect to git - no `git stash`, `checkout`, `reset`, or `rebase`. Keep these
constraints when editing the templates.

GOTCHA - line endings: `.gitattributes` pins these files to `eol=lf`
(`src/ThroughlineBuild.Briefs/Templates/**/*.md text eol=lf`). Snapshot tests in
`tests/ThroughlineBuild.Briefs.Tests` compare exact bytes, so a CRLF edit on
Windows will break snapshots. Edit as LF.

After changing a template, run `dotnet test` for `Briefs.Tests` and update the
snapshots under `tests/ThroughlineBuild.Briefs.Tests/Snapshots/` if the diff is
intended.

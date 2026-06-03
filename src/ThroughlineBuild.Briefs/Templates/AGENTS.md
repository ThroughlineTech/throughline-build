# Briefs/Templates - per-agent brief templates

Per-agent subdirectories: `claude-code/`, `codex/`, `copilot/`, `gemini/`.
Each holds `plan.md`, `implement.md`, `review.md`, `draft.md`, `decompose.md`.
The per-phase builders in the parent project (`PlanBriefBuilder`,
`ImplementBriefBuilder`, etc.) load these via `TemplateLoader` and fill in
project context.

GOTCHA - line endings: `.gitattributes` pins these files to `eol=lf`
(`src/ThroughlineBuild.Briefs/Templates/**/*.md text eol=lf`). Snapshot tests in
`tests/ThroughlineBuild.Briefs.Tests` compare exact bytes, so a CRLF edit on
Windows will break snapshots. Edit as LF.

After changing a template, run `dotnet test` for `Briefs.Tests` and update the
snapshots under `tests/ThroughlineBuild.Briefs.Tests/Snapshots/` if the diff is
intended.

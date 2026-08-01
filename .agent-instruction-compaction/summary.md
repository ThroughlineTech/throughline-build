# Agent Instruction Compaction Summary

Instruction compaction complete.

## Changed Instruction Files

Repository files:

- `AGENTS.md`
- `src/AGENTS.md`
- `tests/AGENTS.md`
- `src/ThroughlineBuild.Briefs/Templates/AGENTS.md`
- `src/ThroughlineBuild.ClaudeCode/AGENTS.md`
- `src/ThroughlineBuild.Cli/AGENTS.md`
- `src/ThroughlineBuild.Contracts/AGENTS.md`
- `src/ThroughlineBuild.Phases/AGENTS.md`
- `src/ThroughlineBuild.Plane/AGENTS.md`
- `src/ThroughlineBuild.Scaffold/AGENTS.md`
- `src/ThroughlineBuild.Verification/AGENTS.md`
- `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md`
- `src/ThroughlineBuild.Workers.Common/AGENTS.md`

External run-backlog skill files:

- `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/SKILL.md`
- `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/parallel-fan-out.md`
- `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/repo-adaptation.md`
- `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/serial-loop.md`

## Generated Comparison Artifacts

- `.agent-instruction-compaction/00-before-step-1/`
- `.agent-instruction-compaction/01-after-rule-inventory/`
- `.agent-instruction-compaction/02-after-root-agents/`
- `.agent-instruction-compaction/03-after-nested-agents/`
- `.agent-instruction-compaction/04-after-skill-router/`
- `.agent-instruction-compaction/05-after-skill-references/`
- `.agent-instruction-compaction/06-after-validation/`
- `.agent-instruction-compaction/rule-inventory.md`
- `.agent-instruction-compaction/final-rule-preservation.md`
- `.agent-instruction-compaction/validation-targeted-search.txt`
- `.agent-instruction-compaction/validation-file-checks.txt`
- `.agent-instruction-compaction/validation-nonascii.txt`
- `.agent-instruction-compaction/validation-skill-quick-validate.txt`
- `.agent-instruction-compaction/word-count-comparison.txt`
- `.agent-instruction-compaction/final-git-status-short.txt`
- `.agent-instruction-compaction/transcript-benchmark-analysis.md`
- `.agent-instruction-compaction/tooling-backlog-recommendations.md`
- `.agent-instruction-compaction/07-transcript-benchmark/analysis.md`
- `.agent-instruction-compaction/07-transcript-benchmark/repeated-blocks.md`
- `.agent-instruction-compaction/07-transcript-benchmark/token-savings-estimate.md`
- `.agent-instruction-compaction/08-tooling-backlog/build-helper-recommendations.md`
- `.agent-instruction-compaction/08-tooling-backlog/proposed-ticket-drafts.md`
- `.agent-instruction-compaction/summary.md`

Each checkpoint contains `files/`, `manifest.sha256`, `word-counts.txt`, and
`analysis.md`; checkpoints 01 through 06 also contain `diff-from-previous.patch`.

## Behavior-Preservation Result

- rules inventoried: 116
- preserved unchanged: 31
- preserved shorter/moved: 77
- intentional duplicates: 8
- needs human review: 0

## Word-Count Result

- before: 6513 words
- after: 5653 words
- savings: 860 words

Checkpoint trend:

```text
.agent-instruction-compaction/00-before-step-1 6513
.agent-instruction-compaction/01-after-rule-inventory 6513
.agent-instruction-compaction/02-after-root-agents 6084
.agent-instruction-compaction/03-after-nested-agents 5624
.agent-instruction-compaction/04-after-skill-router 5692
.agent-instruction-compaction/05-after-skill-references 5653
.agent-instruction-compaction/06-after-validation 5653
```

## Validation

- link/file checks: pass
- targeted search review: pass; audit log has 3179 matches across the requested repo/code/skill scope
- ASCII scan: pass
- skill validation: pass (`Skill is valid!`)
- working tree: dirty as expected, with 13 modified repo instruction files and generated comparison artifacts; existing untracked user files preserved

## Working Tree Status

```text
 M AGENTS.md
 M src/AGENTS.md
 M src/ThroughlineBuild.Briefs/Templates/AGENTS.md
 M src/ThroughlineBuild.ClaudeCode/AGENTS.md
 M src/ThroughlineBuild.Cli/AGENTS.md
 M src/ThroughlineBuild.Contracts/AGENTS.md
 M src/ThroughlineBuild.Phases/AGENTS.md
 M src/ThroughlineBuild.Plane/AGENTS.md
 M src/ThroughlineBuild.Scaffold/AGENTS.md
 M src/ThroughlineBuild.Verification/AGENTS.md
 M src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md
 M src/ThroughlineBuild.Workers.Common/AGENTS.md
 M tests/AGENTS.md
?? .agent-instruction-compaction/
?? agent-instruction-compaction-plan.md
?? backlog-run-analysis-report.md
?? backlog-transcript.txt
?? makin-me-feel-good.txt
```

# Final Rule Preservation

Status values:

- `unchanged`: rule remains in its original owning file with no meaningful change.
- `shorter`: rule remains in place with shorter wording.
- `moved`: rule moved to another file or became an explicit reference to its owner.
- `duplicate`: rule is intentionally duplicated for safety.
- `human_review`: rule needs human review.

Summary counts:

| Status | Count |
| --- | ---: |
| unchanged | 31 |
| shorter | 64 |
| moved | 13 |
| duplicate | 8 |
| human_review | 0 |
| total | 116 |

| Rule | Status | Final location / note |
| --- | --- | --- |
| SH-001 | shorter | `run-backlog/SKILL.md` shell contract |
| SH-002 | unchanged | `run-backlog/SKILL.md` repository-root snippet |
| SH-003 | moved | Parallel reference now inherits `SKILL.md` shell contract |
| SH-004 | moved | Adaptation reference now inherits `SKILL.md` shell contract and keeps inspection rule |
| SH-005 | shorter | Root `AGENTS.md` build prerequisites |
| SH-006 | shorter | Root `AGENTS.md` fresh clone and machine-local props note |
| TK-001 | shorter | Root `AGENTS.md` ticket workflow caveat |
| TK-002 | shorter | Root `AGENTS.md` build-only ticket access ban |
| TK-003 | shorter | Root `AGENTS.md` JSON envelope and exit codes |
| TK-004 | shorter | Root `AGENTS.md` command table plus help reference |
| TK-005 | shorter | Root `AGENTS.md` bare ticket shorthand |
| TK-006 | shorter | Root `AGENTS.md` composite investigate/work flows |
| TK-007 | shorter | Root `AGENTS.md` `build new --print-template` rule |
| TK-008 | duplicate | Root hazard plus `run-backlog/SKILL.md` ticket-client self-change caution |
| TK-009 | shorter | `run-backlog/SKILL.md` command-help contract |
| TK-010 | unchanged | `run-backlog/SKILL.md` and adaptation fake-ticket validation |
| TK-011 | shorter | `run-backlog/SKILL.md` ticket body/comments/AC and self-client caution |
| TK-012 | moved | `run-backlog/references/serial-loop.md` finalize section |
| TK-013 | unchanged | `run-backlog/references/repo-adaptation.md` discovery inputs |
| TK-014 | shorter | `run-backlog/references/repo-adaptation.md` permission/ambiguity rule |
| TK-015 | shorter | `run-backlog/references/repo-adaptation.md` fake-ID/orphan validation |
| BC-001 | shorter | Root `AGENTS.md` branch rule |
| BC-002 | shorter | Root `AGENTS.md` commit-message rule |
| BC-003 | shorter | Root `AGENTS.md` shipping separation |
| BC-004 | duplicate | `run-backlog/SKILL.md` invariant and `agent-contracts.md` worker prompt contract |
| BC-005 | unchanged | `run-backlog/SKILL.md` explicit deploy/push/merge/ship authorization |
| BC-006 | shorter | `run-backlog/SKILL.md` run branch bootstrap |
| BC-007 | moved | `run-backlog/references/serial-loop.md` finalize section |
| BC-008 | unchanged | `run-backlog/references/agent-contracts.md` integration sequence |
| BC-009 | unchanged | `run-backlog/references/agent-contracts.md` integration reviewer limit |
| BC-010 | unchanged | `run-backlog/references/parallel-fan-out.md` serial integration |
| BC-011 | duplicate | `run-backlog/SKILL.md` authorization ban, referenced by fan-out teardown |
| BC-012 | shorter | `run-backlog/references/repo-adaptation.md` irreversible-operation discovery |
| BC-013 | unchanged | `run-backlog/references/repo-adaptation.md` dry-run integration validation |
| WT-001 | duplicate | Root `AGENTS.md` and worker contracts forbid shared concurrent writers |
| WT-002 | shorter | `run-backlog/SKILL.md` mode router |
| WT-003 | moved | `run-backlog/references/serial-loop.md` inventory/order section |
| WT-004 | moved | `run-backlog/references/serial-loop.md` terminal continuation section |
| WT-005 | shorter | `run-backlog/references/parallel-fan-out.md` preconditions |
| WT-006 | unchanged | `run-backlog/references/parallel-fan-out.md` wave planning |
| WT-007 | shorter | `run-backlog/references/parallel-fan-out.md` leasing section |
| WT-008 | unchanged | `run-backlog/references/parallel-fan-out.md` implement/review/rework section |
| WT-009 | unchanged | `run-backlog/references/parallel-fan-out.md` teardown/failure section |
| WT-010 | unchanged | `run-backlog/references/parallel-fan-out.md` planner input/schema |
| WT-011 | unchanged | `run-backlog/references/repo-adaptation.md` repository input discovery |
| WT-012 | unchanged | `run-backlog/references/repo-adaptation.md` Build primitive config |
| WT-013 | unchanged | `run-backlog/references/repo-adaptation.md` no-declaration/temporary-helper rule |
| WT-014 | unchanged | `run-backlog/references/repo-adaptation.md` validation fixture matrix |
| WT-015 | unchanged | `run-backlog/references/repo-adaptation.md` stale worktree/cleanup validation |
| CR-001 | shorter | `run-backlog/SKILL.md` conductor role |
| CR-002 | shorter | `run-backlog/SKILL.md` conductor invariants |
| CR-003 | moved | `run-backlog/references/serial-loop.md` one-ticket handling |
| CR-004 | moved | `run-backlog/references/serial-loop.md` independent review/rework |
| CR-005 | moved | `run-backlog/references/serial-loop.md` finalize section |
| CR-006 | unchanged | `run-backlog/references/agent-contracts.md` conductor state inspection |
| CR-007 | unchanged | `run-backlog/references/agent-contracts.md` implementer contract |
| CR-008 | duplicate | Root/skill concurrency rules plus worker contract |
| CR-009 | unchanged | `run-backlog/references/agent-contracts.md` reviewer contract |
| CR-010 | unchanged | `run-backlog/references/agent-contracts.md` rework rule |
| CR-011 | unchanged | `run-backlog/references/agent-contracts.md` adapter warnings |
| CR-012 | unchanged | `run-backlog/references/repo-adaptation.md` adapter installation guidance |
| CR-013 | duplicate | `run-backlog/SKILL.md` short router note and detailed `agent-contracts.md` adapter warning |
| GV-001 | shorter | Root `AGENTS.md` build/test gates |
| GV-002 | duplicate | Root gate-vacuity warning and Verification local behavior note |
| GV-003 | shorter | `tests/AGENTS.md` test-suite command/orientation |
| GV-004 | moved | `run-backlog/references/serial-loop.md` hygiene/implementation/review gate rules |
| GV-005 | moved | `run-backlog/references/serial-loop.md` evidence rule |
| GV-006 | unchanged | `run-backlog/references/agent-contracts.md` implementer gate command |
| GV-007 | unchanged | `run-backlog/references/agent-contracts.md` reviewer gate/verdict |
| GV-008 | unchanged | `run-backlog/references/parallel-fan-out.md` leased worktree gate rule |
| GV-009 | unchanged | `run-backlog/references/repo-adaptation.md` real `build gate` config rule |
| GV-010 | unchanged | `run-backlog/references/repo-adaptation.md` validation matrix |
| GV-011 | shorter | `src/ThroughlineBuild.Verification/AGENTS.md` stack-agnostic rule |
| GV-012 | shorter | `src/ThroughlineBuild.Verification/AGENTS.md` vacuity/control behavior |
| GV-013 | shorter | `src/ThroughlineBuild.Verification/AGENTS.md` ratifier/tool-enforcement behavior |
| AOT-001 | shorter | Root `AGENTS.md` AOT/source-generated JSON rule |
| AOT-002 | moved | `src/AGENTS.md` now cross-references root AOT rule |
| AOT-003 | shorter | `tests/AGENTS.md` reflection-off test pattern |
| AOT-004 | shorter | `src/ThroughlineBuild.ClaudeCode/AGENTS.md` facade AOT/contract rule |
| AOT-005 | duplicate | Root AOT rule plus Plane local source-generated JSON reminder |
| AOT-006 | shorter | `src/ThroughlineBuild.Scaffold/AGENTS.md` reflection switch test note |
| AOT-007 | shorter | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md` parser route |
| AOT-008 | shorter | `src/ThroughlineBuild.Workers.Common/AGENTS.md` parser contract |
| AOT-009 | shorter | `src/ThroughlineBuild.Workers.Common/AGENTS.md` no reflection Markdown lib |
| DOC-001 | shorter | Root `AGENTS.md` current/historical/help authority |
| DOC-002 | moved | `src/AGENTS.md` shorter current-vs-historical reminder |
| DOC-003 | shorter | `src/ThroughlineBuild.Cli/AGENTS.md` help subsystem/add-verb contract |
| DOC-004 | shorter | `run-backlog/SKILL.md` help contract |
| DOC-005 | unchanged | `run-backlog/references/repo-adaptation.md` command-help syntax warning |
| TXT-001 | shorter | Root `AGENTS.md` ASCII-only rule |
| TXT-002 | shorter | `tests/AGENTS.md` snapshot/LF rule |
| TXT-003 | shorter | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` embedded-resource warning |
| TXT-004 | shorter | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` git-state bans |
| TXT-005 | shorter | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` LF/snapshot bytes rule |
| TXT-006 | shorter | `src/ThroughlineBuild.Scaffold/AGENTS.md` out-of-tree docs embed |
| TXT-007 | shorter | `src/ThroughlineBuild.Scaffold/AGENTS.md` template LF/rebuild/tests rule |
| TXT-008 | shorter | `src/ThroughlineBuild.Verification/AGENTS.md` ratify prompt LF/rebuild rule |
| TXT-009 | shorter | `src/ThroughlineBuild.Workers.Common/AGENTS.md` UTF-8 child stream note |
| RP-001 | shorter | Root `AGENTS.md` authority/nested override rule |
| RP-002 | shorter | Root `AGENTS.md` repo identity and stack-agnostic design |
| RP-003 | shorter | Root `AGENTS.md` read-before-writing rule |
| RP-004 | shorter | Root `AGENTS.md` solution/test local fakes rule |
| RP-005 | shorter | Root `AGENTS.md` no nested worker-spawning verbs |
| RP-006 | shorter | `src/AGENTS.md` project count/dependency order/solution reminder |
| RP-007 | shorter | `tests/AGENTS.md` local doubles and console redirection rule |
| RP-008 | shorter | `src/ThroughlineBuild.Cli/AGENTS.md` CLI owner boundaries |
| RP-009 | shorter | `src/ThroughlineBuild.Cli/AGENTS.md` pre-config verbs and worker wiring |
| RP-010 | shorter | `src/ThroughlineBuild.Contracts/AGENTS.md` leaf/no-I/O/WorkspaceSchema rule |
| RP-011 | shorter | `src/ThroughlineBuild.Phases/AGENTS.md` phase/orchestration ownership |
| RP-012 | shorter | `src/ThroughlineBuild.Phases/AGENTS.md` serial chain and writer-routing gotchas |
| RP-013 | shorter | `src/ThroughlineBuild.Plane/AGENTS.md` Plane ownership/no adapters |
| RP-014 | shorter | `src/ThroughlineBuild.Plane/AGENTS.md` cache/throttle/retry/schema/relation rules |
| RP-015 | shorter | `src/ThroughlineBuild.Scaffold/AGENTS.md` parser/validator/profile derivation |
| RP-016 | shorter | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md` transport/preflight/state ownership |
| RP-017 | unchanged | `run-backlog/SKILL.md` final report contents |
| RP-018 | unchanged | `run-backlog/agents/openai.yaml` UI metadata |

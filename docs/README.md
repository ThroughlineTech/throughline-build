# Documentation

The documents in this repository serve different audiences and were written at
different points in the project's development. This page is the reading map.

## Current references

Start with these documents when evaluating or using the current source tree:

| Document | Purpose |
| --- | --- |
| [Architecture](throughline-build-architecture.md) | As-built design, trust boundaries, components, and invariants |
| [Operator user guide](throughline_build_userguide.md) | Installation, configuration, and day-to-day workflows |
| [Building from source](build-command-setup.md) | Contributor prerequisites, build, test, and Native AOT publish commands |
| [Agent adapters](build-agent-tool-name-mapping.md) | Shared worker contract and provider-specific behavior |
| [Event log](build-event-log-format.md) | Durable JSONL event format |
| [Debug transcript](build-debug-transcript-format.md) | Worker transcript format and redaction behavior |
| [Worker result envelope](build-worker-result-envelope.md) | Structured result protocol returned by workers |
| [Recursive chains](build-grandparent-chain.md) | Tree scheduling, dependencies, depth, and branch topology |
| [Tree-aware behavior](build-tree-aware-behavior.md) | Parent and child workflow rules |
| [Bring your own conductor](bring-your-own-conductor.md) | Deterministic worktree leases for an external agent loop |

The source and `build help <topic>` remain authoritative when a current
reference and the executable disagree.

## Point-in-time material

- [State of the system](state-of-the-system/00-index.md) is a detailed snapshot
  stamped with the commit it describes. It is useful for historical spelunking,
  but it is not the current architecture authority.
- [Analysis](analysis/README.md) contains measured experiments, methods, and
  findings. Each result should be read with its recorded workload and
  limitations.
- [Research](research/) contains design investigations and proposals. These are
  working notes, not shipped commitments.
- [Operation documents](op-docs/) are planning artifacts for specific bodies of
  work. They preserve design context but may describe superseded paths or
  line numbers.
- [Project history](history.md) records the evolution of the implementation.

Historical documents are kept because they show the reasoning and evidence
behind the system. Their dates, commit stamps, and status language matter.

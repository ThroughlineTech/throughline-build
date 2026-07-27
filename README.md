# Throughline Build

Throughline Build is a .NET 10 Native AOT CLI named `build`. It coordinates
Plane tickets, git worktrees, automated checks, and selectable coding-agent
CLIs through plan, implement, review, ship, and recursive chain workflows.

For normal installation, configuration, and a first-ticket walkthrough, start
with the [operator user guide](docs/throughline_build_userguide.md). Contributors
building the CLI itself should use [Building from source](docs/build-command-setup.md).

## Creating tickets

Create a ticket from a short request:

```console
build new fix the README typo
```

For a structured Markdown draft:

```console
build new --print-template > draft.md
build new draft.md
```

For automation, `build new - --json` accepts the strict JSON draft described by
`build help new`.

## Documentation

- [Operator user guide](docs/throughline_build_userguide.md) - configuration and everyday workflow
- [Building from source](docs/build-command-setup.md) - contributor build, test, and publish commands
- [Architecture](docs/throughline-build-architecture.md) - as-built components and invariants
- Wire and diagnostic references: [event log](docs/build-event-log-format.md), [debug transcript](docs/build-debug-transcript-format.md), and [worker result envelope](docs/build-worker-result-envelope.md)
- [Worker agent adapters](docs/build-agent-tool-name-mapping.md) - shared contract and provider differences
- [Recursive chain deep dive](docs/build-grandparent-chain.md) - tree scheduling, dependencies, depth, and branch topology

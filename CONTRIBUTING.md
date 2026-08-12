# Contributing

Thanks for taking the time to improve Throughline Build.

## Before you start

- Read [AGENTS.md](AGENTS.md) for repository invariants and working conventions.
  Nested `AGENTS.md` files under [src/](src/AGENTS.md) and
  [tests/](tests/AGENTS.md) add local detail and win inside their subtree.
- Open an issue before beginning a large behavioral change.
- Keep the engine stack-agnostic: language and framework knowledge belongs in
  project data and briefs, not in the orchestration core.
- Work on a topic branch; do not commit directly to `main`.
- Read before writing. Do not propose changes to code you have not read.

## Local setup

Install the SDK selected by `global.json` and a native toolchain for Native AOT
publish (MSVC on Windows, Xcode command line tools on macOS, clang or gcc on
Linux). `git` must be on `PATH`.

`bin/` is gitignored, so a fresh clone has no binary: build one with
`./build.sh` before running `build` against this repository.

The tracked `.build/config.toml` points at the maintainers' Plane project, so
the ticket verbs (`new`, `list`, `transition`, `chain`, and the rest) will not
authenticate for an outside contributor. Use GitHub issues and pull requests
instead; nothing in the required checks depends on a ticket backend.

Root `Directory.Build.props` and `Directory.Build.targets` are machine-local AOT
linker overrides. Tracked `tests/Directory.Build.props` chains to them only when
they exist, so their absence is normal.

## Required checks

```sh
dotnet restore --locked-mode --nologo -v q
dotnet build throughline-build.sln --no-restore --nologo -v q
dotnet test throughline-build.sln --no-build --no-restore --nologo -v q
dotnet format throughline-build.sln --no-restore --verify-no-changes -v q
python tools/check_markdown_links.py
python tools/publication_audit.py
```

Run the full test suite, not a filtered subset, and quote the real output in the
pull request. A partial or filtered run is not a passing gate.

CI runs restore, test, `dotnet format`, a Native AOT publish, and
`publication_audit.py` on `osx-arm64`, `win-x64`, and `linux-x64`. Changes that
affect the CLI's AOT surface must pass a Native AOT publish for your platform
before review:

```sh
dotnet publish src/ThroughlineBuild.Cli -r linux-x64 -c Release --nologo -v q
```

See [Building from source](docs/build-command-setup.md) for the full matrix.

## Code conventions

- Native AOT: `ThroughlineBuild.Cli` publishes with `PublishAot=true`. Use
  source-generated `JsonSerializerContext`; never rely on reflection-based
  serialization. Keep `ThroughlineBuild.Contracts` I/O-free. AOT-sensitive tests
  must flip reflection off explicitly; see [tests/AGENTS.md](tests/AGENTS.md).
- `throughline-build.sln` is the source of truth for projects. A directory under
  `src/` that is absent from the solution is local debris, not a component.
- Tests are per-project: each library under `src/` has a mirrored suite under
  `tests/` with local fakes. Reuse the existing doubles rather than adding a
  shared test framework.
- Gates must be able to fail. Zero gating checks pass silently, and a check that
  cannot be proven to fail on broken input is treated as vacuous. When you touch
  gate, scaffold, or verification code, confirm the failure path still fails.
- ASCII only in anything you write: code, comments, docs, commit messages, and
  ticket bodies. No em or en dashes, no curly quotes. When editing a file that
  already contains non-ASCII, read-modify-write through a file rather than a
  shell variable.

## Commits

Use `{TICKET-ID}: short description` when the work tracks a ticket, and
`topic: short description` otherwise. Do not add agent-vendor branding,
generated-with lines, or `Co-Authored-By` trailers for an agent.

Implementing and shipping are separate steps. Do not merge, push to `main`, tag,
or cut a release without explicit instruction.

## Pull requests

Keep each pull request focused. Describe the behavior change, the validation
performed, and any compatibility or AOT implications.

Update current documentation whenever a verb, config key, contract, or status
tag changes. Source and generated help are authoritative: current references in
[docs/](docs/README.md) describe HEAD, while the state-of-the-system set is a
stamped historical snapshot and should not be rewritten to match new behavior.
The operator user guide is generated - edit
`src/ThroughlineBuild.Commands/Templates/throughline_build_userguide.md` and
keep `docs/throughline_build_userguide.md` byte-identical to it.

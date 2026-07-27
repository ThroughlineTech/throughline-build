# Contributing

Thanks for taking the time to improve Throughline Build.

## Before you start

- Read [AGENTS.md](AGENTS.md) for repository invariants and working conventions.
- Open an issue before beginning a large behavioral change.
- Keep the engine stack-agnostic: language and framework knowledge belongs in
  project data and briefs, not in the orchestration core.
- Work on a topic branch; do not commit directly to `main`.

## Required checks

Install the SDK selected by `global.json`, then run:

```sh
dotnet restore --locked-mode --nologo -v q
dotnet build throughline-build.sln --no-restore --nologo -v q
dotnet test throughline-build.sln --no-build --no-restore --nologo -v q
dotnet format throughline-build.sln --no-restore --verify-no-changes -v q
python tools/check_markdown_links.py
python tools/publication_audit.py
```

Changes that affect the CLI's AOT surface must also pass a Native AOT publish
for the contributor's platform. See
[Building from source](docs/build-command-setup.md).

## Pull requests

Keep each pull request focused. Describe the behavior change, the validation
performed, and any compatibility or AOT implications. Update current
documentation whenever a verb, config key, contract, or status tag changes.

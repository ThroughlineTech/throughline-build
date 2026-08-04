# New repository setup: Plane and run-backlog

Use this runbook to prepare a repository that:

- reads and updates tickets through Plane;
- has usable Claude Code and Codex worker definitions;
- exposes the binary-hosted `run-backlog` SOP to both Claude and Codex; and
- has a real, blocking repository gate.

Run every command from the repository root. The current binary is the authority;
use `build <command> --help` if its command contract differs from this document.

## Understand the files

Two TOML files have different owners and must not be combined:

| File | Contents | Git policy |
| --- | --- | --- |
| `.build/config.toml` | Plane connection, token environment-variable name, workers, events, and executable review checks | Machine-local and ignored |
| `.build/conductor.toml` | SOP version floor, ticket and branch prefixes, source roots, architecture map, review invariants, and escalation paths | Tracked |

The SOP installer also emits two tracked host stubs and one installer cache:

- `.claude/commands/run-backlog.md` for Claude;
- `.agents/skills/run-backlog/SKILL.md` for Codex; and
- `.build/sop-manifest.json`, a cache of what a prior install wrote.

The embedded catalog, not the manifest, is the authority for emitted stub
content. Never put a Plane token in `conductor.toml`, a host stub, or a temporary
TOML file that Git can see.

## 1. Check prerequisites

Install `build`, Git, Claude Code, and Codex, then confirm that the same shell can
find them:

```sh
build --version
git --version
claude --version
codex --version
```

Claude's default `interactive-hook` transport requires Claude Code 2.1.177 or
newer. Authenticate Claude and Codex through their own CLIs before asking Build
to start either worker.

Have these Plane values ready:

- API base URL;
- workspace slug;
- existing project UUID, or a project name to find or create; and
- a personal API token.

Also decide the repository's ticket prefix, branch prefix, source roots,
architecture-map path, contract authority, and actual build and test commands.

## 2. Put the Plane token in the environment

In Git Bash, read the token without echoing it or placing its value in shell
history:

```sh
read -rsp "Plane API token: " PLANE_API_TOKEN
export PLANE_API_TOKEN
printf '\n'
```

The important distinction is:

```text
shell environment:  PLANE_API_TOKEN=<the secret value>
config.toml:         plane_api_token_env = "PLANE_API_TOKEN"
```

`--token-env` takes the environment variable's name. Do not pass
`--token-env "$PLANE_API_TOKEN"`; that expands the secret and incorrectly makes
the token itself the environment-variable name.

## 3. Create `.build/config.toml` and connect Plane

For an existing Plane project, run:

```sh
build init --no-interactive \
  --plane-url "PLANE_API_URL" \
  --workspace "WORKSPACE_SLUG" \
  --project-id "PROJECT_UUID" \
  --token-env PLANE_API_TOKEN
```

Replace the uppercase placeholders before running the command. An explicit
`--project-id` uses the offline initialization path: it writes the complete
configuration but does not contact or provision Plane. Follow it with:

```sh
build setup
```

To find or create a project by name and perform setup in one connected command,
omit `--project-id` and use `--project-name` instead:

```sh
build init --no-interactive \
  --plane-url "PLANE_API_URL" \
  --workspace "WORKSPACE_SLUG" \
  --project-name "PROJECT_NAME" \
  --token-env PLANE_API_TOKEN
```

Connected initialization resolves or creates the project, writes its UUID,
provisions the required Plane states and labels, initializes Git when needed,
and verifies connectivity.

Do not use `build init --print-template` as the installation step. It prints a
template to stdout and writes no configuration. Do not redirect that template
to `conductor.toml`; it is a `config.toml` template.

If `.build/config.toml` already exists, stop and inspect it. `build init` refuses
to overwrite it unless `--force` is supplied. Do not use `--force` on an
established repository until its project-specific `[[review.checks]]` have been
preserved; the generated template intentionally contains no active checks.

## 4. Verify the full runtime configuration

Confirm that no scaffold placeholders remain:

```sh
rg -n "REQUIRED_" .build/config.toml
```

The command must produce no output. Confirm that the ticketing entry names the
environment variable rather than its value:

```toml
[ticketing]
plane_api_token_env = "PLANE_API_TOKEN"
```

The generated config includes both `[workers.claude-code]` and
`[workers.codex]`, including their required `small`, `medium`, and `large` size
maps. Keep both definitions when both CLIs will be used. `[workers].default_agent`
selects Build's default worker; it does not control which SOP host stubs are
installed.

Use `build models refresh` after Codex is installed and authenticated if the
generated Codex model tiers need refreshing.

Verify local and Plane setup, then perform a real ticket read:

```sh
build setup --check
build list --json
```

`build list` is the connectivity check that matters here. A successful
`build sop doctor` does not prove Plane connectivity because SOP commands do not
load ticketing credentials, workers, or events.

## 5. Configure a blocking gate

`run-backlog` refuses to proceed without at least one setup or gating check.
Replace the generated config's empty review-check area with commands that really
build or test this repository. For example, only in a repository where
`npm test` is a real gate:

```toml
[review]
verifier_timeout_minutes = 15
verifier_allowed_tools = ["Read", "Grep", "Glob"]

[[review.checks]]
name = "test"
executable = "npm"
arguments = ["test"]
timeout_minutes = 10
role = "gating"
```

Use the repository's actual toolchain instead of copying an inapplicable
example. A zero-check gate is not a successful setup. Run it before installing
the SOP:

```sh
build gate --require-checks --json
```

## 6. Install run-backlog for Claude and Codex

Install the named SOP without a host filter:

```sh
build sop install --sop run-backlog --json
```

Omitting `--host` installs both known host adapters. The expected stubs are:

```text
.claude/commands/run-backlog.md
.agents/skills/run-backlog/SKILL.md
```

The explicit two-command equivalent is:

```sh
build sop install --sop run-backlog --host claude --json
build sop install --sop run-backlog --host codex --json
```

Install is idempotent. It creates `.build/conductor.toml` only when that file is
missing and never overwrites an existing conductor file.

## 7. Replace the conductor scaffold with repository facts

Open `.build/conductor.toml` and replace every generic scaffold value. At a
minimum, verify:

- `min_build_version` is no newer than the installed binary;
- `branch_prefix` matches the repository's branch convention;
- `ticket_prefix` matches the Plane project identifier;
- `source_roots` covers the code and current architecture documentation;
- `architecture_map` names a tracked, current file;
- every review invariant is true and repository-specific;
- escalation paths identify the repository's high-risk surfaces;
- `platform` describes the target platform; and
- `contract_authority` names the real shared-contract authority.

Do not leave the scaffold sentence that says to replace it with a true review
invariant. `conductor.toml` must contain no secrets and should be committed with
the host stubs.

## 8. Verify the completed installation

Run all of these checks; they prove different things:

```sh
build setup --check
build list --json
build gate --require-checks --json
build sop status --sop run-backlog --json
build sop doctor --json
build sop brief run-backlog --json
```

The expected result is:

- Plane setup and a ticket read succeed;
- at least one real gating check executes and passes;
- SOP status reports no drift;
- doctor reports no conductor, stub, or review-check findings; and
- the brief succeeds and includes `data.sopText`.

Start a fresh Claude or Codex session from the repository root after installing
the stubs so the host discovers the new command or skill.

## 9. Check Git ownership before committing

Run:

```sh
git status --short
git check-ignore -v .build/config.toml
```

The local `.build/config.toml` must be ignored. Review and commit the tracked
repository contract and host stubs:

```text
.build/conductor.toml
.claude/commands/run-backlog.md
.agents/skills/run-backlog/SKILL.md
```

Treat `.build/sop-manifest.json` as installer history, not as authority for
modified stubs. Follow the repository's chosen policy on tracking that cache,
and never use it to bless content that differs from the embedded catalog.

Before committing, confirm that no temporary configuration file contains a
literal Plane token and that no broad `.build/` ignore rule hides the tracked
`conductor.toml`.

## Troubleshooting

### `missing required TOML section [ticketing]`

The nearest `.build/config.toml` is partial. Ticket commands use the full
configuration and require `[ticketing]`, `[workers]`, and `[events]`. Re-run
initialization for a genuinely new repository, or merge the missing sections
without replacing existing review checks.

### `required environment variable '<token text>' is not set`

`plane_api_token_env` contains the secret value instead of the variable name.
Set it to:

```toml
plane_api_token_env = "PLANE_API_TOKEN"
```

Then export the token value as `PLANE_API_TOKEN` in the same shell that runs
Build.

### `sop doctor` passes but `build list` fails

This is possible by design. Doctor validates conductor data, emitted stubs, and
the shape of `[[review.checks]]`; it does not load or test Plane configuration.
Use `build setup --check` and `build list --json` to verify the runtime and
ticketing configuration.

### Doctor reports `review.checks.empty`

The generated configuration has no active checks. Add at least one real setup
or gating check and verify it with `build gate --require-checks --json`.

### Only one host stub exists

Run the install command for the missing host, or rerun the unfiltered command:

```sh
build sop install --sop run-backlog --json
```

Then use `build sop status --sop run-backlog --json` to confirm both catalog
paths are present and unmodified.

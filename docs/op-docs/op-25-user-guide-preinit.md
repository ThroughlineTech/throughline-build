# Operation: user-guide-command

Add a `build user-guide` verb that writes an embedded user-guide markdown file to `docs/throughline_build_userguide.md` in the operator's working directory, creating `docs/` if needed. The user guide is two sections: prerequisites the operator gathers and installs before running `build init`, and a first-ticket walkthrough that takes them from `init` through `ship`. Refuses to clobber, opt-in `--force`. Mirrors `build init`'s operator-surface conventions. Closes the onboarding gap by giving every TLB binary a self-contained answer to "how do I start?"

## Why this exists

Today the only way to know how to start with TLB is to read the README and `.build/config.toml.example` in the source repo. That works when you `git clone` the repo; it does not work when you download a binary (the release infrastructure op-doc enables that). The user guide closes the gap: every binary carries an embedded markdown file that tells the operator what to gather, what to install, and the exact command sequence to run their first ticket end-to-end.

Scope is deliberately tight: prerequisites plus a first-ticket walkthrough that smoke-tests the install. Not a verb reference, not advanced features, not chain/decompose patterns. The operator who runs `build user-guide` after downloading the binary should be able to follow the document to a successful `build ship` on their first ticket without needing any other documentation.

The init command lands first (TLB-316 / TLB-317); this op-doc references the values init will prompt for so the guide's prerequisites section matches what init will ask the operator to provide.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | User guide: content + embedded resource + verb | - | S |

Single small plan; briefs sequential.

## Plan A: User guide

### Goal

The user-guide markdown lives in the TLB source tree as the single source of truth, is embedded into the CLI assembly at build time, and is writable to the operator's repo via `build user-guide`. After this plan, an operator who has downloaded a `build` binary can run `build user-guide` in a fresh project directory and have a working first-ticket walkthrough to follow.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | user-guide-content | Author the user-guide markdown: prerequisites section + first-ticket walkthrough | - | src/ThroughlineBuild.Commands/Resources/throughline_build_userguide.md (or repo-side equivalent path; implementer chooses) |
| 02 | embedded-resource-wiring | Embed the markdown as a resource in the CLI assembly with an AOT-safe loader | 01 | src/ThroughlineBuild.Commands/ThroughlineBuild.Commands.csproj, src/ThroughlineBuild.Commands/UserGuideLoader.cs (new) |
| 03 | user-guide-verb-and-init-pointer | `build user-guide` verb that writes the embedded guide to docs/; init mentions the verb on success | 02 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |

### Briefs - detail

#### Brief 01: user-guide-content

Goal: A markdown file in the TLB source tree that is the canonical user-onboarding document - two scannable sections covering prerequisites and a first-ticket walkthrough. This file is the single source of truth that the embedded-resource brief reads from.

Inputs: the init command's flag set (TLB-316 init-config-template defines the placeholders; TLB-317 build-init-command defines the prompt set: plane base URL, workspace slug, project ID, API token or token-env, default agent, agent executable); the verb set the walkthrough exercises (`new`, `plan`, `implement`, `review`, `ship`); the existing `.build/config.toml.example` for parameter naming consistency.

Outputs:
- A markdown file at the chosen source-tree path. Structure: title, brief introduction, a "Prerequisites" section split into "things to install" (git, the chosen worker agent CLI such as claude-code, codex, gemini, or copilot) and "things to gather" (Plane base URL, workspace slug, project ID, API token plan, default agent name, agent executable path) with one or two sentences explaining each, and a "First ticket walkthrough" section with the exact command sequence: `build init` → `build new "<title>"` → verify the ticket appears in Plane → `build plan <id>` → `build implement <id>` → `build review <id>` → `build ship <id>` → verify the commit. Each step says what the operator should observe before moving to the next.
- Header note pointing at `.build/config.toml.example` and the binary's release page so the reader knows where to look for deeper config detail and version info.
- File written in TLB's existing markdown conventions (single hyphens, no em-dashes, plain markdown).

Acceptance:
- [ ] The file exists in the TLB source tree at the chosen path
- [ ] The prerequisites section names every value the init command will prompt for
- [ ] The prerequisites section names the install-side dependencies (git and the chosen worker agent CLI)
- [ ] The walkthrough's command sequence executes end-to-end against a real Plane project with no missing steps, no commands that error
- [ ] The file is under approximately 200 lines

Notes: This file is the single source of truth for the user-onboarding story. The embedded-resource brief reads from this file at build time; if this file drifts from operator reality, every binary that ships carries the drift. Verify the walkthrough end-to-end against a real Plane project at least once before declaring the brief done. The "what to observe" framing at each step matters - "you should see ticket 1 in Plane with title X and state Backlog" gives the operator a checkpoint, "now run `build plan 1`" does not.

OOS:
- Comprehensive verb reference covering every flag of every command (scope is prereqs plus first ticket only)
- Documentation for advanced features (chain, decompose, scaffold, rework)
- Translation or multi-language variants
- Embedding the file as a resource (B02 owns)

#### Brief 02: embedded-resource-wiring

Goal: The user-guide markdown is embedded as a resource in the CLI assembly so the running binary can write it to disk without depending on the repo or any external file at runtime.

Inputs: the markdown file from B01; the existing embedded-resource patterns in `ThroughlineBuild.Commands` (`BodyTemplateLoader` and `ConfigTemplateLoader` from the init work); AOT-safe stream loading via `Assembly.GetManifestResourceStream`.

Outputs:
- csproj entry embedding the markdown file as an `EmbeddedResource` in `ThroughlineBuild.Commands` (or wherever the user-guide verb's logic lives per B03). The entry uses a path or `LogicalName` that produces a predictable, discoverable resource name.
- `UserGuideLoader` class parallel to `BodyTemplateLoader` and `ConfigTemplateLoader`: a static class with a lazy-cached `Load()` method that reads the embedded stream and returns the markdown content as a string.
- The loader throws `InvalidOperationException` with a clear message if the embedded resource is missing (build-time misconfiguration, not a runtime situation).
- The loading path is AOT-safe: works in a `dotnet publish -r <rid> --self-contained -p:PublishAot=true` build, not only in `dotnet run`.

Acceptance:
- [ ] The loader returns the exact content of the source markdown file from B01
- [ ] The loader works in an AOT-published binary on at least one platform
- [ ] The missing-resource case produces a clear error message rather than null or empty content
- [ ] The embedded resource is discoverable in the published assembly's manifest

Notes: Resource path syntax in csproj must use forward slashes or the MSBuild path-normalization that works cross-platform; backslash-only paths break Linux/macOS builds. Match the existing template-loading pattern's resource-name convention so a future reader of `BodyTemplateLoader.cs`, `ConfigTemplateLoader.cs`, and `UserGuideLoader.cs` sees a consistent shape.

OOS:
- Versioning or revision tracking of the embedded guide (the binary's version implies which guide it carries)
- Multi-file documentation embedding (one file only for now; if more is wanted later, the loader pattern extends)
- Hot-reload or runtime template overrides
- The CLI verb dispatch (B03 owns)

#### Brief 03: user-guide-verb-and-init-pointer

Goal: A `build user-guide` verb that writes the embedded markdown to `docs/throughline_build_userguide.md` in the current working directory, plus a one-line pointer at the end of `build init`'s success output so operators who run init first discover the user-guide verb.

Inputs: the `UserGuideLoader` from B02; the early-dispatch verb pattern from the init work (verb runs before any config load is required); the init command's success-message output.

Outputs:
- `build user-guide` verb dispatched in `Program.cs`, positioned in the same early-dispatch region as `build init` so it runs without requiring a `.build/config.toml` to exist.
- Default behavior: write to `Path.Combine(cwd, "docs", "throughline_build_userguide.md")`. Create `docs/` if missing. If the target file already exists and `--force` is not set, exit 2 with a clear error and leave the existing file untouched. On success, print the absolute path written and exit 0.
- `--force` flag overwrites an existing file.
- `--print-template` flag writes the guide to stdout instead of the filesystem (mirroring init's flag).
- Usage text in `CliUsage.cs` documents the verb and its flags following the existing format.
- `build init`'s success-message output ends with a one-line pointer naming the user-guide verb so operators reading init's output discover it without needing to consult the verb list.

Acceptance:
- [ ] `build user-guide` in a directory without an existing guide writes the file to `docs/throughline_build_userguide.md` and exits 0
- [ ] The `docs/` directory is created if it does not already exist
- [ ] The verb runs successfully without a `.build/config.toml` present
- [ ] Re-running the verb without `--force` exits 2 and leaves the existing file untouched
- [ ] `--force` overwrites the existing file successfully
- [ ] `--print-template` writes the content to stdout without touching the filesystem
- [ ] `build init`'s success output mentions the user-guide verb
- [ ] `build --help` lists the user-guide verb with its flags

Notes: The verb's operator surface (`--force`, `--print-template`, refuse-to-clobber default) is symmetric with the init verb so an operator who knows one knows the other. The output path is fixed to `docs/throughline_build_userguide.md`; if a project uses a different documentation directory, the operator can move the file afterward - making this configurable would invite scope creep without operator benefit. The init pointer is one line; do not pile additional cross-references into init's output.

OOS:
- Configurable output path
- Multi-file documentation generation
- Auto-updating the guide based on the installed verb set at runtime (the embedded content is what the binary was built with)
- Localized or translated variants

## What done looks like

An operator who has downloaded a `build` binary runs `build user-guide` in a fresh project directory, gets a markdown file at `docs/throughline_build_userguide.md`, and follows it through `build init` → first ticket → `build ship` without consulting any other documentation. Running `build init` ends with a one-line pointer at the user-guide verb so operators who tried init first still discover the guide. The guide's content lives in one place in the TLB source tree, is embedded into every released binary, and stays in lockstep with the init verb's prompt set because both are documented from the same source.
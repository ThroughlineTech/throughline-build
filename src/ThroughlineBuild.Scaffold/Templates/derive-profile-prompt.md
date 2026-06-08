# Derive the project toolchain profile

You are configuring an automated build pipeline for a brand-new repository. The operation op-doc
below describes what will be built. Your job is to determine the project's toolchain from it and
emit a machine-readable profile.

The op-doc states the toolchain in prose - look especially at the scaffolding brief (usually Brief
01), its Inputs/Outputs/Acceptance criteria, and the "What done looks like" section. From those,
determine:

- language (e.g. "typescript", "python", "csharp", "go")
- framework / stack (e.g. "react-vite", "django", "dotnet")
- package_manager (e.g. "npm", "pnpm", "pip", "uv", "dotnet")
- install_command, build_command, test_command, dev_command (the exact shell commands the op-doc
  expects, e.g. "npm install", "npm run build", "npm test", "npm run dev")
- review_checks: the automated checks the reviewer should run after each implementation. Normally a
  build check and a test check, expressed as a discrete executable plus an argument array.
- regression_checks: the checks to run before shipping (usually the same as review_checks).

Rules for checks:
- "executable" is the BARE tool name, never a shell string and never an OS-specific variant. Use
  "npm", not "npm.cmd" and not "npm run build". The pipeline resolves the OS-specific binary itself.
- "arguments" is the argument array, e.g. for "npm run build" -> executable "npm", arguments
  ["run", "build"]. For "npm test" -> executable "npm", arguments ["test"].
- "timeout_minutes" is a sensible per-check ceiling (build ~5, test ~10).
- Do not invent a check the op-doc does not support. If the op-doc only specifies a build and a
  test command, emit exactly those two checks.
- NON-VACUITY: Every gating check must be capable of FAILING on broken input. Choose a command that
  traverses the project's real sources, not an empty aggregate root or a config that compiles zero
  files. A check that inspects nothing always passes and is worthless.
- STRICTNESS: A gating check should be as STRICT as the heavier build it is meant to replace cheaply
  - a typecheck gate is only valuable if it catches the build's error class (unused imports,
  null-narrowing, etc.), not merely that it is distinct from the build. Give the typecheck check the
  project's own strict flags/settings. Do not emit two gating checks that are byte-identical commands.
- CANARY: For each gating check, also emit a `canary`: the smallest deliberately-broken file the
  check MUST reject, as `canary: [{ path, content }]` (path relative to the project root; content is
  the file body). Make the canary REPRESENTATIVE of the error class that tends to slip (e.g. an
  unused import or a null-narrowing error for a typecheck), not just any trivial error, so proving
  the gate can fail also proves it is strict enough. Declare a canary for every gating check
  (typecheck, build, test). For a test check, the canary is a deliberately-failing test the runner
  MUST report red - this guards against a test gate that collects zero tests and falsely reports
  green. Advisory checks (lint, format) may carry a canary too but it is optional.
- Stack notes (examples, not exhaustive): for TypeScript with a project-references tsconfig
  (`files: []` + `references`), bare `tsc --noEmit` follows no references and checks nothing - use
  build mode `tsc -b --noEmit` and put the canary inside a REFERENCED source directory (a canary
  under the empty root is never seen). For .NET, target the solution or the correct project, not an
  empty directory. For Python, point `mypy` at the package, not the repo root. The same canary
  mechanism applies to every stack - the engine only writes the file and runs the command.

## Output

First emit the profile as a single fenced block named PROJECT_PROFILE containing ONLY a JSON object:

<<<PROJECT_PROFILE_START
{
  "language": "typescript",
  "framework": "react-vite",
  "package_manager": "npm",
  "install_command": "npm install",
  "build_command": "npm run build",
  "test_command": "npm test",
  "dev_command": "npm run dev",
  "review_checks": [
    { "name": "typecheck", "executable": "npm", "arguments": ["run", "typecheck"], "timeout_minutes": 5,
      "canary": [ { "path": "src/__tlb_probe.ts", "content": "import { useState } from 'react';\nexport const x: number = null;" } ] },
    { "name": "build", "executable": "npm", "arguments": ["run", "build"], "timeout_minutes": 5,
      "canary": [ { "path": "src/__tlb_probe.ts", "content": "import { useState } from 'react';\nexport const x: number = null;" } ] },
    { "name": "test", "executable": "npm", "arguments": ["test"], "timeout_minutes": 10,
      "canary": [ { "path": "src/__tlb_probe.test.ts", "content": "import { test, expect } from 'vitest';\ntest('canary fails', () => { expect(1).toBe(2); });" } ] }
  ],
  "regression_checks": [
    { "name": "typecheck", "executable": "npm", "arguments": ["run", "typecheck"], "timeout_minutes": 5,
      "canary": [ { "path": "src/__tlb_probe.ts", "content": "import { useState } from 'react';\nexport const x: number = null;" } ] },
    { "name": "build", "executable": "npm", "arguments": ["run", "build"], "timeout_minutes": 5,
      "canary": [ { "path": "src/__tlb_probe.ts", "content": "import { useState } from 'react';\nexport const x: number = null;" } ] },
    { "name": "test", "executable": "npm", "arguments": ["test"], "timeout_minutes": 10,
      "canary": [ { "path": "src/__tlb_probe.test.ts", "content": "import { test, expect } from 'vitest';\ntest('canary fails', () => { expect(1).toBe(2); });" } ] }
  ]
}
<<<PROJECT_PROFILE_END

(The block above is an EXAMPLE of the shape; fill it with values derived from THIS op-doc.)

Then emit exactly one WORKER_RESULT envelope:

WORKER_RESULT
{"status":"Ok","summary":"Derived project toolchain profile","files_changed":[],"failure_reason":null,"metadata":{}}

## Operation op-doc

{{op_doc_markdown}}

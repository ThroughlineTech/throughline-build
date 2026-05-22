# Workspace Codex Agent Instructions

Project identifier: TLB

This file is written to <workspace>/AGENTS.md by /ticket-install
(and refreshed by install.sh --sync-templates when drift is detected).

---

## Project config files

The following files in .claude/ define this workspace's ticket workflow:

  .claude/plane-config.md   - Plane project UUIDs, state IDs, label IDs,
                              workspace slug, and pre-built view URLs.
  .claude/ticket-config.md  - Stack, build/test/deploy/lint commands,
                              and preview profiles.

Read these files before running any ticket-* command against this workspace.

---

## Bare numeric ticket references

Bare numbers in this workspace refer to tickets in the TLB project.
Expand them to full identifiers before passing to any command:

  35  ->  TLB-35
  101 ->  TLB-101

Example: "/ti 35 36" investigates tickets TLB-35 and TLB-36.

---

## Global rules

For the slash-command dispatch rule, repo locator, Plane auth rules,
and Windows shell guidance that apply in every workspace, see:

  ~/.codex/AGENTS.md

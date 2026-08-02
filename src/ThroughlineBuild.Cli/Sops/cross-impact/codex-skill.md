---
name: cross-impact
description: Determine whether a change affects sibling platforms through the binary-hosted cross-impact SOP.
---

# cross-impact

Run `build sop brief cross-impact --json` from the repository you are operating on and follow the returned SOP text exactly.

If `build` is missing, exits nonzero, reports an unknown SOP, reports that doctor failed, or omits `data.sopText`, stop and report the failure. Do not use cached prose and do not improvise a fallback.

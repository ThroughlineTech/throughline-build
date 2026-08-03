# Cross-impact: does this change touch a sibling platform? (`cross-impact`)

This is the agent-agnostic procedure for the CROSS-IMPACT standard operating procedure in a
multi-repo constellation that shares one wire contract (e.g. the Rejog web / ios / android
trio). Your job: determine whether a change touches another platform, answer it from the
ACTUAL sibling code (freshly pulled, strictly read-only), and DRAFT - not silently create -
any follow-up tickets the other platforms need.

It holds no repo specifics. The brief envelope's `data.conductor.constellation` names the origin
platform, sibling repos with their paths and ticket prefixes, and the wire-contract authority. Use
that typed data; do not fall back to cached prose or memory.

## Input you are given

- **CHANGE UNDER REVIEW**: a ticket id or a short description (passed by the human / calling
  entry point). If it is a ticket id, fetch it from the ORIGIN's Plane project (`build get <ID>`)
  for the real scope first.

## Procedure

1. **IDENTITY.** From the brief envelope: your platform and the constellation (the sibling repos,
   their paths and ticket prefixes, and the wire-contract authority). The repo you are in is the
   ORIGIN; the others are SIBLINGS you investigate strictly read-only.

2. **SCOPE.** Decide which sibling(s) the change could plausibly touch, and why:
   - a wire-contract / shape change -> every platform that consumes the shape;
   - a shared user-facing behavior -> the platform(s) that render it;
   - "does feature X already exist / is it planned elsewhere?" -> the named platform.

   If nothing plausibly crosses platforms, say so and STOP - this is a no-op.

3. **REFRESH each in-scope sibling before reading** (never reason off stale code), READ-ONLY.
   For each sibling path from `data.conductor.constellation.siblings`:
   ```
   git -C <path> fetch
   git -C <path> switch main && git -C <path> pull --ff-only
   git -C <path> rev-parse --short HEAD     # record this SHA in your report
   ```
   NEVER edit / stage / commit / branch in a sibling. If a sibling path does NOT exist on this
   machine, say so and fall back to that platform's Plane project + the shared contract (or ask
   the human) - do not guess from memory.

4. **INVESTIGATE.** Grep/read the sibling source, its `AGENTS.md`, and the wire contract.
   Answer concretely: does the feature exist there, does the ORIGIN change break it, does it
   need a change there - citing `file:line @ SHA`.

5. **REPORT.** Per sibling: a classified verdict (confirmed gap / latent footgun / intentional
   design / no-impact / nit), the evidence, and the recommended action.

6. **DRAFT cross-ref tickets - do NOT auto-create across projects without the human's go.**
   For each platform that needs work, draft the ticket for THAT platform's Plane project: a
   title + body, with the greppable text convention baked in (Plane cannot cross-link):
   - new ticket body: `cross-ref: <ORIGIN-TICKET>`
   - origin ticket note: `Filed <NEW-TICKET> (<platform>) for <the change>`

   Present the drafts and ASK before creating. On confirmation, create each in its project via
   the `build` CLI run from that repo's directory (use a subshell:
   `(cd <sibling-path> && build new - --json)`), then post the back-reference
   note on the origin ticket. NEVER implement the other platform's code yourself.

End with: which siblings you checked (+ the SHAs), the per-platform verdict, and the exact
tickets you drafted or (on confirmation) created.

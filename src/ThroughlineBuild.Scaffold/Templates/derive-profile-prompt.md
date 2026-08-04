# Derive the project toolchain profile

You are configuring an automated build pipeline for a brand-new repository. The operation op-doc
below describes what will be built. Read its scaffolding brief, inputs, outputs, acceptance criteria,
and "What done looks like" section to determine the project's toolchain.

{{profile_rules}}

## Worker response protocol

First emit the profile as a single named block containing ONLY the JSON object:

<<<PROJECT_PROFILE_START
{ ... profile JSON ... }
<<<PROJECT_PROFILE_END

Then emit exactly one WORKER_RESULT envelope. Emit the block and envelope together in the final
message, and write nothing after the envelope JSON:

WORKER_RESULT
{"status":"Ok","summary":"Derived project toolchain profile","files_changed":[],"failure_reason":null,"metadata":{}}

## Operation op-doc

{{op_doc_markdown}}

# ThroughlineBuild.Scaffold - op-doc -> ticket tree

`ScaffoldPhase` parses op-doc markdown with hand-rolled, line-oriented
`OpDocParser` (errors gathered), validates with `OpDocValidator` (warnings block
unless `--accept-warnings`), and creates the plan-ticket plus brief-ticket tree
in Plane. `--dry-run` makes zero API calls. `OpDocSkeletonGenerator` backs
`build op-doc new`.

`ScaffoldProfileDeriver` is the LLM step: a worker emits a `PROJECT_PROFILE`
fenced block inside the standard `WORKER_RESULT`; parsing then becomes
deterministic. Cli's `ScaffoldProfileRunner` drives it and `ConfigProfileWriter`
writes it.

GOTCHA - embedded out-of-tree docs: the csproj embeds
`docs/op-docs/op-doc-spec.md` from outside this project. The canonical example is
fenced inside the spec and extracted by `OpDocDocsLoader.LoadExample`; editing
the guide changes `build op-doc spec` output and validation examples only after
rebuild.

GOTCHA - `Templates/*.md` are EmbeddedResource and LF-pinned. Edit as LF and
rebuild. Tests live in `tests/ThroughlineBuild.Scaffold.Tests`; some flip the
AOT reflection switch off, and that pattern must remain.

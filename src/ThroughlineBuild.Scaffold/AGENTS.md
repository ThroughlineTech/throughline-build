# ThroughlineBuild.Scaffold - op-doc -> ticket tree

`ScaffoldPhase` parses an op-doc markdown file (`OpDocParser`, hand-rolled and
line-oriented; errors gathered, not thrown), validates it (`OpDocValidator`;
warnings block unless `--accept-warnings`), and creates the plan-ticket +
brief-ticket tree in Plane. `--dry-run` previews with zero API calls.
`OpDocSkeletonGenerator` backs `build op-doc new`.

`ScaffoldProfileDeriver` is the LLM step: a worker agent reads the op-doc and
emits the project's toolchain profile (commands + review checks) as a
`PROJECT_PROFILE` fenced block under the standard WORKER_RESULT envelope;
parsing is then deterministic. Driven from Cli's `ScaffoldProfileRunner`,
written by `ConfigProfileWriter`.

GOTCHA - embedded out-of-tree docs: the csproj embeds
`docs/op-docs/op-doc-spec.md` and `op-doc-example.md` from OUTSIDE this
project as resources. Editing those docs changes binary behavior (`build
op-doc spec` output, validation examples) only after a rebuild.

GOTCHA - `Templates/*.md` (skeleton fragments + derive-profile prompt) are
EmbeddedResource and LF-pinned via `.gitattributes` - edit as LF, rebuild to
pick up changes. Tests: `tests/ThroughlineBuild.Scaffold.Tests` (some flip
the AOT reflection switch off - keep that pattern).

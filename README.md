To Test:
dotnet test

To build the native cli:
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release

That produces the build.exe native binary (project has <PublishAot>true</PublishAot> and <AssemblyName>build</AssemblyName> in ThroughlineBuild.Cli.csproj).

Swap the RID for other platforms: -r osx-arm64, -r linux-x64.

If you just want to compile-check without producing a native binary: dotnet build throughline-build.sln.

## Creating tickets

Use the built-in body template to start a new ticket with the correct structure:

    build new --print-template > draft.md

Edit draft.md to fill in the title, description, acceptance criteria, and out-of-scope sections,
then create the ticket:

    build new draft.md

The template uses the section headings that the NewPhase validator recognises (title via a top-level
`#` heading, `## Acceptance criteria`, `## Out of scope`). Filling those sections avoids the
"missing acceptance criteria" warning that appears when creating a ticket from a bare file.

Codex install:
Windows: powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
Mac: curl -fsSL https://chatgpt.com/codex/install.sh | sh

Gemini install:
Mac/Windows: npm install -g @google/gemini-cli --or-- npx @google/gemini-cli

CoPilot install:
https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli
Mac/Windows: npm install -g @github/copilot


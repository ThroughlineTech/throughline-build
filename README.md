To Test:
dotnet test

To build the native cli:
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release

That produces the build.exe native binary (project has <PublishAot>true</PublishAot> and <AssemblyName>build</AssemblyName> in ThroughlineBuild.Cli.csproj).

Swap the RID for other platforms: -r osx-arm64, -r linux-x64.

If you just want to compile-check without producing a native binary: dotnet build throughline-build.sln.


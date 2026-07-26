# Building from source

This page is for contributors building the `build` CLI from this repository. For
installation, configuration, and normal command usage, see the
[Throughline Build user guide](throughline_build_userguide.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git on `PATH`
- A native compiler toolchain when publishing the Native AOT executable:
  - Windows: Visual Studio Build Tools with the C++ workload
  - macOS: Xcode Command Line Tools
  - Linux: GCC or Clang and the platform development libraries required by .NET

A worker CLI and Plane credentials are not required to compile or run the test
suite. They are required when you run ticket phases; the user guide covers that
setup.

## Restore, build, and test

Run these commands from the repository root:

```console
dotnet restore throughline-build.sln
dotnet build throughline-build.sln --nologo -v q
dotnet test --nologo -v q --logger "console;verbosity=minimal"
```

Run the CLI directly from source:

```console
dotnet run --project src/ThroughlineBuild.Cli -- --help
```

The extra `--` separates `dotnet run` options from arguments passed to `build`.

## Publish a native executable

Publish the CLI for the current target explicitly:

```console
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release --nologo -v q
```

The project has Native AOT enabled, so the publish output contains a native
`build` executable (`build.exe` on Windows). Substitute an appropriate runtime
identifier such as `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`.

The repository script publishes `build` and the companion tools, copies them to
`bin/`, and installs them to `$HOME/.local/bin` unless `INSTALL_DIR` is set:

```bash
./build.sh
```

On Windows, run the script with Git Bash. From PowerShell, use the explicit Git
Bash path so a WSL `bash.exe` earlier on `PATH` is not selected:

```powershell
& 'C:\Program Files\Git\bin\bash.exe' ./build.sh
```

The script publishes binaries; run the test command separately before relying
on the output.

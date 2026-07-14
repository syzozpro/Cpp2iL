# Rosetta

A CLI tool for decompiling IL2CPP Unity game builds (APK/XAPK) back into readable, approximate C# source.

Unlike simple metadata dumpers, Rosetta implements a full decompilation pipeline — from raw machine code up to structured, near-source-level output.

## Pipeline

Rosetta reconstructs C# through five stages:

1. **Custom Decoder** — disassembles native ARM/x86 instructions directly from the IL2CPP binary, independent of external disassembler dependencies.
2. **IR Lifter** — translates decoded instructions into an architecture-agnostic intermediate representation, normalizing register and memory operations for analysis.
3. **CFG Construction** — builds a control flow graph per method, resolving branches, loops, and jump tables into structured basic blocks.
4. **SSA Form** — converts the CFG into Static Single Assignment form, enabling accurate variable tracking and simplifying downstream expression reconstruction.
5. **AST Generation** — rebuilds a high-level abstract syntax tree from SSA form, emitted as approximate, readable C#.

This staged architecture is what separates Rosetta from simpler IL2CPP tools that only recover method signatures or shallow bytecode — it aims to recover actual method logic.

## Features

- **Full-logic script decompilation** — recovers approximate C# source (not just stubs/signatures) from `Assembly-CSharp` inside an IL2CPP build.
- **IL/IR dumping** — inspect intermediate representation output for debugging or research.
- **32-bit and 64-bit architecture targeting.**
- **Per-method verbose tracing** for debugging specific decompilation targets.

## Requirements

- .NET 8 SDK (or later) to build from source.
- No runtime install needed if you use one of the published self-contained binaries under [Releases](../../releases).

## Usage

Decompile scripts from a given assembly:

```bash
dotnet run -c Release -- scripts "/path/to/Game.apk" --select Assembly-CSharp
```

Target a 32-bit build, or dump the intermediate representation:

```bash
dotnet run -c Release -- scripts "/path/to/Game.apk" --select Assembly-CSharp --arch-32
dotnet run -c Release -- scripts "/path/to/Game.apk" --select Assembly-CSharp --dump-ir
```

Verbose output for a specific method:

```bash
dotnet run -c Release -- scripts "/path/to/Game.apk" --select Assembly-CSharp --verbose "gen:Namespace##MethodName"
```

## Building from source

```bash
dotnet build -c Release
```

## Published binaries

Self-contained, single-file builds are published with each [Release](../../releases), covering:

- `win-x64`, `win-arm64`
- `osx-x64`, `osx-arm64`
- `linux-x64`, `linux-arm64`

## Building a release yourself

```bash
dotnet publish -c Release -r <RID> \
  -p:PublishAot=false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --self-contained
```

Replace `<RID>` with any of the runtime identifiers listed above.

## Intended use

This tool is meant for interoperability, modding, translation, and preservation work on games you own or otherwise have the rights to inspect. You're responsible for complying with the terms of any game you run it against.

## License

[GNU GPLv3](LICENSE) — see the LICENSE file for details.

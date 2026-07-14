# Rosetta

A CLI tool for decompiling IL2CPP Unity game builds (APK/XAPK) back into approximate C# source.

## Features

- **Script decompilation** — recovers approximate C# source from `Assembly-CSharp` inside an IL2CPP build, with optional IL/IR dumping and 32-bit architecture targeting.

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

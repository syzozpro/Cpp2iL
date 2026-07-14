/// IPlatformStructure — Interface for all platform-specific game structure handlers.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms;

public interface IPlatformStructure
{
    /// Platform type this structure represents.
    PlatformType Platform { get; }
    /// Detected game name (from folder structure or metadata).
    string? Name { get; }
    /// Root path of the extracted/discovered game data.
    string RootPath { get; }
    /// Path to the game's Data folder.
    string? GameDataPath { get; }
    /// Path to StreamingAssets folder.
    string? StreamingAssetsPath { get; }
    /// Detected scripting backend.
    ScriptBackend Backend { get; }
    /// Path to libunity.so / UnityPlayer.dll.
    string? UnityPlayerPath { get; }
    /// Path to libil2cpp.so / GameAssembly.dll.
    string? Il2CppBinaryPath { get; }
    /// Path to global-metadata.dat.
    string? MetadataPath { get; }
    /// All discovered files with their categories.
    List<DiscoveredFile> Files { get; }

    /// Collect all game files from the root path.
    void CollectFiles(bool skipStreamingAssets = false);
}

/// A single discovered file with its path and category.
public record DiscoveredFile(string FullPath, string Name, FileCategory Category);

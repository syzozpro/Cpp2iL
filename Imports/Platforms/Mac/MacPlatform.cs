/// Mac — macOS .app bundle game structure detection and file collection.
/// Detects GameName.app/Contents/Resources/Data directory.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.Mac;

public sealed class MacPlatform : PlatformBase
{
    public override PlatformType Platform => PlatformType.Mac;

    const string ContentsName = "Contents";
    const string FrameworksName = "Frameworks";
    const string AppExtension = ".app";
    const string Il2CppDataFolder = "il2cpp_data";

    public MacPlatform(string rootPath) : base(rootPath)
    {
        // rootPath should be the .app bundle
        string contentsPath = Path.Combine(rootPath, ContentsName);
        string resourcesPath = Path.Combine(contentsPath, ResourcesName);
        string dataPath = Path.Combine(resourcesPath, DataFolderName);

        if (!Directory.Exists(dataPath))
            throw new DirectoryNotFoundException($"macOS data directory not found: {dataPath}");

        Name = Path.GetFileNameWithoutExtension(rootPath);
        GameDataPath = dataPath;
        StreamingAssetsPath = Path.Combine(GameDataPath, StreamingName);

        // macOS IL2CPP: GameAssembly.dylib in Contents/Frameworks/
        string frameworksDir = Path.Combine(contentsPath, FrameworksName);
        string il2cppBinary = Path.Combine(frameworksDir, "GameAssembly.dylib");
        string il2cppMeta = Path.Combine(GameDataPath, Il2CppDataFolder, MetadataFolderName, DefaultGlobalMetadataName);

        if (File.Exists(il2cppBinary) && File.Exists(il2cppMeta))
        {
            Il2CppBinaryPath = il2cppBinary;
            MetadataPath = il2cppMeta;
            Backend = ScriptBackend.IL2Cpp;
        }
        else
        {
            string managedDir = Path.Combine(GameDataPath, ManagedName);
            if (Directory.Exists(managedDir) && Directory.EnumerateFiles(managedDir, "*.dll").Any())
                Backend = ScriptBackend.Mono;
        }

        // UnityPlayer.dylib
        string unityPlayer = Path.Combine(frameworksDir, "UnityPlayer.dylib");
        if (File.Exists(unityPlayer))
            UnityPlayerPath = unityPlayer;
    }

    public override void CollectFiles(bool skipStreamingAssets = false)
    {
        base.CollectFiles(skipStreamingAssets);

        if (GameDataPath != null)
        {
            AddIfExists(GameDataPath, "unity_builtin_extra", FileCategory.Asset);
            AddIfExists(GameDataPath, "unity default resources", FileCategory.Asset);

            string resourcesDir = Path.Combine(GameDataPath, ResourcesName);
            if (Directory.Exists(resourcesDir))
            {
                AddIfExists(resourcesDir, "unity_builtin_extra", FileCategory.Asset);
                AddIfExists(resourcesDir, "unity default resources", FileCategory.Asset);
            }

            string managedDir = Path.Combine(GameDataPath, ManagedName);
            CollectAssemblies(managedDir);
        }

        if (Il2CppBinaryPath != null)
            AddFile(Il2CppBinaryPath, "GameAssembly.dylib", FileCategory.Lib);
        if (MetadataPath != null)
            AddFile(MetadataPath, DefaultGlobalMetadataName, FileCategory.Metadata);
    }

    /// Check if a directory has a macOS .app bundle structure.
    public static bool IsMacStructure(string path)
    {
        if (!Directory.Exists(path)) return false;
        if (!path.EndsWith(AppExtension, StringComparison.Ordinal)) return false;

        string dataPath = Path.Combine(path, ContentsName, ResourcesName, DataFolderName);
        return Directory.Exists(dataPath);
    }
}

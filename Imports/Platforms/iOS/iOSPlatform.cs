/// iOS — iOS IPA game structure detection and file collection.
/// Detects Payload/GameName.app/Data structure.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.iOS;

public sealed class iOSPlatform : PlatformBase
{
    public override PlatformType Platform => PlatformType.iOS;

    const string PayloadName = "Payload";
    const string AppExtension = ".app";
    const string iOSStreamingName = "Raw";

    public iOSPlatform(string rootPath) : base(rootPath)
    {
        if (!GetDataDirectory(rootPath, out string? dataPath, out string? appPath, out string? name))
            throw new DirectoryNotFoundException($"iOS data directory not found under: {rootPath}");

        Name = name;
        GameDataPath = dataPath;
        StreamingAssetsPath = Path.Combine(rootPath, iOSStreamingName);

        // iOS IL2CPP: the executable inside .app is the IL2CPP binary
        string il2cppBinary = Path.Combine(appPath!, name!);
        string managedDir = Path.Combine(dataPath!, ManagedName);
        string il2cppMeta = Path.Combine(managedDir, MetadataFolderName, DefaultGlobalMetadataName);

        if (File.Exists(il2cppBinary) && File.Exists(il2cppMeta))
        {
            Il2CppBinaryPath = il2cppBinary;
            MetadataPath = il2cppMeta;
            Backend = ScriptBackend.IL2Cpp;
        }
        else
        {
            if (Directory.Exists(managedDir) && Directory.EnumerateFiles(managedDir, "*.dll").Any())
                Backend = ScriptBackend.Mono;
        }
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
            AddFile(Il2CppBinaryPath, Path.GetFileName(Il2CppBinaryPath), FileCategory.Lib);
        if (MetadataPath != null)
            AddFile(MetadataPath, DefaultGlobalMetadataName, FileCategory.Metadata);
    }

    /// Check if a directory has an iOS game structure.
    public static bool IsIOSStructure(string path)
    {
        if (!Directory.Exists(path)) return false;
        return GetDataDirectory(path, out _, out _, out _);
    }

    /// Find: Payload/GameName.app/Data
    private static bool GetDataDirectory(string rootDir, out string? dataPath, out string? appPath, out string? name)
    {
        string payloadPath = Path.Combine(rootDir, PayloadName);
        if (Directory.Exists(payloadPath))
        {
            foreach (string dir in Directory.EnumerateDirectories(payloadPath))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.EndsWith(AppExtension, StringComparison.Ordinal))
                {
                    appPath = dir;
                    name = dirName[..^AppExtension.Length];
                    dataPath = Path.Combine(dir, DataFolderName);
                    if (Directory.Exists(dataPath))
                        return true;
                }
            }
        }

        dataPath = null;
        appPath = null;
        name = null;
        return false;
    }
}

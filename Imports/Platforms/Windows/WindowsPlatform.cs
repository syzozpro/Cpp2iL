/// Windows — Windows PC game structure detection and file collection.
/// Detects GameName_Data directory alongside GameName.exe.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.Windows;

public sealed class WindowsPlatform : PlatformBase
{
    public override PlatformType Platform => PlatformType.Windows;

    const string ExeExtension = ".exe";
    const string Il2CppDataFolder = "il2cpp_data";

    public WindowsPlatform(string rootPath) : base(GetActualRoot(rootPath))
    {
        if (!GetDataDirectory(RootPath, out string? dataPath, out string? name))
            throw new DirectoryNotFoundException($"Windows data directory not found under: {RootPath}");

        Name = name;
        GameDataPath = dataPath ?? "";
        StreamingAssetsPath = Path.Combine(dataPath!, StreamingName);

        // Windows IL2CPP paths
        string il2cppBinary = Path.Combine(RootPath, DefaultGameAssemblyName);
        string il2cppMeta = Path.Combine(GameDataPath, Il2CppDataFolder, MetadataFolderName, DefaultGlobalMetadataName);

        if (File.Exists(il2cppBinary) && File.Exists(il2cppMeta))
        {
            Il2CppBinaryPath = il2cppBinary;
            MetadataPath = il2cppMeta;
            Backend = ScriptBackend.IL2Cpp;
        }
        else
        {
            // Mono backend
            string managedDir = Path.Combine(GameDataPath, ManagedName);
            if (Directory.Exists(managedDir) && Directory.EnumerateFiles(managedDir, "*.dll").Any())
                Backend = ScriptBackend.Mono;
        }

        // UnityPlayer.dll
        string unityPlayer = Path.Combine(RootPath, DefaultUnityPlayerName);
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

            // Managed assemblies
            string managedDir = Path.Combine(GameDataPath, ManagedName);
            CollectAssemblies(managedDir);
        }

        // IL2CPP files
        if (Il2CppBinaryPath != null)
            AddFile(Il2CppBinaryPath, DefaultGameAssemblyName, FileCategory.Lib);
        if (MetadataPath != null)
            AddFile(MetadataPath, DefaultGlobalMetadataName, FileCategory.Metadata);
    }

    /// Check if a directory has a Windows game structure.
    public static bool IsWindowsStructure(string path)
    {
        if (!Directory.Exists(path)) return false;

        // If pointing at an .exe file, get the parent directory
        string dir = path;
        if (File.Exists(path) && path.EndsWith(ExeExtension, StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(path) ?? path;

        // If pointing at a *_Data directory, check parent
        if (dir.EndsWith("_Data", StringComparison.Ordinal) && Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? dir;

        return GetDataDirectory(dir, out _, out _);
    }

    // ── Helpers ──

    /// Resolve to the actual root if user passed an .exe or *_Data path.
    private static string GetActualRoot(string rootPath)
    {
        if (File.Exists(rootPath) && rootPath.EndsWith(ExeExtension, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(rootPath) ?? rootPath;

        if (rootPath.EndsWith($"_{DataFolderName}", StringComparison.Ordinal) && Directory.Exists(rootPath))
            return Path.GetDirectoryName(rootPath) ?? rootPath;

        return rootPath;
    }

    /// Find the data directory. Pattern: GameName.exe ↔ GameName_Data/
    private static bool GetDataDirectory(string rootDir, out string? dataPath, out string? name)
    {
        // Look for .exe files with matching _Data folders
        if (Directory.Exists(rootDir))
        {
            foreach (string file in Directory.EnumerateFiles(rootDir))
            {
                if (!file.EndsWith(ExeExtension, StringComparison.OrdinalIgnoreCase)) continue;

                string exeName = Path.GetFileNameWithoutExtension(file);
                string dataFolder = $"{exeName}_{DataFolderName}";
                string candidate = Path.Combine(rootDir, dataFolder);
                if (Directory.Exists(candidate))
                {
                    dataPath = candidate;
                    name = exeName;
                    return true;
                }
            }

            // Fallback: just look for a "Data" folder (Unity standalone default)
            string fallback = Path.Combine(rootDir, DataFolderName);
            if (Directory.Exists(fallback))
            {
                dataPath = fallback;
                name = Path.GetFileName(rootDir);
                return true;
            }
        }

        dataPath = null;
        name = null;
        return false;
    }
}

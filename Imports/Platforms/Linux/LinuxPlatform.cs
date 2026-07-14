/// Linux — Linux game structure detection and file collection.
/// Detects GameName.x86_64 (or .x86/.x64) + GameName_Data directory.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.Linux;

public sealed class LinuxPlatform : PlatformBase
{
    public override PlatformType Platform => PlatformType.Linux;

    const string Il2CppDataFolder = "il2cpp_data";
    static readonly string[] LinuxExeExtensions = { ".x86_64", ".x86", ".x64" };

    public LinuxPlatform(string rootPath) : base(GetActualRoot(rootPath))
    {
        if (!GetDataDirectory(RootPath, out string? dataPath, out string? name))
            throw new DirectoryNotFoundException($"Linux data directory not found under: {RootPath}");

        Name = name;
        GameDataPath = dataPath ?? "";
        StreamingAssetsPath = Path.Combine(dataPath!, StreamingName);

        // Linux IL2CPP paths
        string il2cppBinary = Path.Combine(RootPath, "GameAssembly.so");
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

        // UnityPlayer.so
        string unityPlayer = Path.Combine(RootPath, "UnityPlayer.so");
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
            AddFile(Il2CppBinaryPath, "GameAssembly.so", FileCategory.Lib);
        if (MetadataPath != null)
            AddFile(MetadataPath, DefaultGlobalMetadataName, FileCategory.Metadata);
    }

    /// Check if a directory has a Linux game structure.
    public static bool IsLinuxStructure(string path)
    {
        if (!Directory.Exists(path)) return false;
        string dir = GetActualRoot(path);
        return GetDataDirectory(dir, out _, out _);
    }

    private static string GetActualRoot(string rootPath)
    {
        if (File.Exists(rootPath) && IsLinuxExecutable(rootPath))
            return Path.GetDirectoryName(rootPath) ?? rootPath;

        if (rootPath.EndsWith($"_{DataFolderName}", StringComparison.Ordinal) && Directory.Exists(rootPath))
            return Path.GetDirectoryName(rootPath) ?? rootPath;

        return rootPath;
    }

    private static bool IsLinuxExecutable(string path)
        => LinuxExeExtensions.Any(ext => path.EndsWith(ext, StringComparison.Ordinal));

    /// Find GameName.x86_64 + GameName_Data/
    private static bool GetDataDirectory(string rootDir, out string? dataPath, out string? name)
    {
        if (Directory.Exists(rootDir))
        {
            foreach (string file in Directory.EnumerateFiles(rootDir))
            {
                string ext = Path.GetExtension(file);
                if (LinuxExeExtensions.Contains(ext))
                {
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
            }
        }

        dataPath = null;
        name = null;
        return false;
    }
}

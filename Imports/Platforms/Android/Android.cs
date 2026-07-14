/// Android — Android APK/OBB game structure detection and file collection.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.Android;

public sealed class Android : PlatformBase
{
    public override PlatformType Platform => PlatformType.Android;

    // Android-specific folder names (from AR)
    const string AssetDir = "assets";
    const string MetaInfDir = "META-INF";
    const string BinDir = "bin";

    public string? LibPath { get; private set; }

    /// All discovered native lib architectures for this APK.
    public List<NativeLib> Il2CppLibs { get; } = new();
    public List<NativeLib> UnityLibs { get; } = new();

    /// The selected (best) architecture.
    public Architecture SelectedArch { get; private set; } = Architecture.Unknown;

    readonly string? _obbRoot;

    public Android(string rootPath, string? obbPath = null, bool prefer32Bit = false) : base(rootPath)
    {
        string apkDataPath = Path.Combine(rootPath, AssetDir, BinDir, DataFolderName);
        if (!Directory.Exists(apkDataPath))
            throw new DirectoryNotFoundException($"Android data directory not found: {apkDataPath}");

        GameDataPath = apkDataPath;
        StreamingAssetsPath = null;
        LibPath = Path.Combine(rootPath, LibName);

        // Discover ALL native libs across all architectures
        Il2CppLibs.AddRange(NativeLibNames.DiscoverAll(LibPath, NativeLibNames.Il2CppAndroid));
        UnityLibs.AddRange(NativeLibNames.DiscoverAll(LibPath, NativeLibNames.UnityAndroid));

        // Pick best (prefer 64-bit by default, or 32-bit if specified)
        var bestIl2Cpp = NativeLibNames.PickBest(Il2CppLibs, prefer32Bit);
        var bestUnity = NativeLibNames.PickBest(UnityLibs, prefer32Bit);

        Il2CppBinaryPath = bestIl2Cpp?.FullPath;
        UnityPlayerPath = bestUnity?.FullPath;
        SelectedArch = bestIl2Cpp?.Arch ?? bestUnity?.Arch ?? Architecture.Unknown;

        // Find metadata
        string metaDir = Path.Combine(GameDataPath, ManagedName, MetadataFolderName);
        string metaFile = Path.Combine(metaDir, DefaultGlobalMetadataName);
        MetadataPath = File.Exists(metaFile) ? metaFile : null;

        // Detect scripting backend
        if (Il2CppBinaryPath != null && MetadataPath != null
            && File.Exists(Il2CppBinaryPath) && File.Exists(MetadataPath))
        {
            Backend = ScriptBackend.IL2Cpp;
        }
        else
        {
            string managedDir = Path.Combine(GameDataPath, ManagedName);
            Backend = Directory.Exists(managedDir) && Directory.EnumerateFiles(managedDir, "*.dll").Any()
                ? ScriptBackend.Mono : ScriptBackend.Unknown;
        }

        // Handle OBB
        if (obbPath != null && Directory.Exists(obbPath))
        {
            _obbRoot = obbPath;
        }
    }

    public override void CollectFiles(bool skipStreamingAssets = false)
    {
        base.CollectFiles(skipStreamingAssets);

        // Collect APK asset bundles (from assets/ root, excluding bin/)
        string assetPath = Path.Combine(RootPath, AssetDir);
        if (Directory.Exists(assetPath))
        {
            CollectBundles(assetPath);
            foreach (string subDir in Directory.EnumerateDirectories(assetPath))
            {
                if (Path.GetFileName(subDir) == BinDir) continue;
                CollectBundlesRecursive(subDir);
            }
        }

        // AR loads hex-named serialized files from Data/ on demand via RequestDependency.
        // We don't have lazy loading, so scan Data/ for extra serialized files upfront.
        // These are Unity's split serialized files (e.g. "bf98650d5a96e4d548882e012f6e7f80")
        // containing the bulk of game assets (materials, textures, animations, etc.)
        if (GameDataPath != null)
            CollectExtraSerializedFiles(GameDataPath);

        // Collect engine files
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
        }

        // Collect ALL discovered native libs (all architectures)
        foreach (var lib in Il2CppLibs)
            AddFile(lib.FullPath, $"{lib.FileName} ({lib.Arch})", FileCategory.Lib);
        foreach (var lib in UnityLibs)
            AddFile(lib.FullPath, $"{lib.FileName} ({lib.Arch})", FileCategory.Lib);
        if (MetadataPath != null)
            AddFile(MetadataPath, DefaultGlobalMetadataName, FileCategory.Metadata);

        // Collect managed assemblies
        if (GameDataPath != null)
        {
            string managedDir = Path.Combine(GameDataPath, ManagedName);
            CollectAssemblies(managedDir);
        }

        // Collect OBB data
        if (_obbRoot != null)
        {
            string obbDataPath = Path.Combine(_obbRoot, AssetDir, BinDir, DataFolderName);
            if (Directory.Exists(obbDataPath))
            {
                CollectCompressedGameFiles(obbDataPath);
                CollectSerializedFiles(obbDataPath);
                CollectResourceFiles(obbDataPath);
            }
        }
    }

    /// Check if a directory has an Android game structure.
    public static bool IsAndroidStructure(string path)
    {
        if (!Directory.Exists(path)) return false;
        string dataPath = Path.Combine(path, AssetDir, BinDir, DataFolderName);
        return Directory.Exists(dataPath);
    }
}

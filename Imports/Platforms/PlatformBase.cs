/// PlatformBase — Abstract base class for all platform structures.
using System.Text.RegularExpressions;
using Rosetta.Extractor.Imports.Enums;
// using Rosetta.Extractor.Reader.Assets;

namespace Rosetta.Extractor.Imports.Platforms;

public abstract partial class PlatformBase : IPlatformStructure
{
    // ── Properties ──
    public abstract PlatformType Platform { get; }
    public string? Name { get; protected set; }
    public string RootPath { get; }
    public string? GameDataPath { get; protected set; }
    public string? StreamingAssetsPath { get; protected set; }
    public ScriptBackend Backend { get; protected set; } = ScriptBackend.Unknown;
    public string? UnityPlayerPath { get; protected set; }
    public string? Il2CppBinaryPath { get; protected set; }
    public string? MetadataPath { get; protected set; }
    public List<DiscoveredFile> Files { get; } = new();

    // ── Well-known file/folder names (from AR PlatformGameStructure) ──
    protected const string DataFolderName = "Data";
    protected const string ManagedName = "Managed";
    protected const string LibName = "lib";
    protected const string ResourcesName = "Resources";
    protected const string StreamingName = "StreamingAssets";
    protected const string MetadataFolderName = "Metadata";
    protected const string DefaultGlobalMetadataName = "global-metadata.dat";
    protected const string DefaultUnityPlayerName = "UnityPlayer.dll";
    protected const string DefaultGameAssemblyName = "GameAssembly.dll";

    protected const string MainDataName = "mainData";
    protected const string GlobalGameManagersName = "globalgamemanagers";
    protected const string GlobalGameManagerAssetsName = "globalgamemanagers.assets";
    protected const string ResourcesAssetsName = "resources.assets";
    protected const string LevelPrefix = "level";
    protected const string DataBundleName = "data.unity3d";
    protected const string DataPackBundleName = "datapack.unity3d";

    protected const string AssetBundleExtension = ".unity3d";
    protected const string AlternateBundleExtension = ".bundle";

    protected PlatformBase(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Root directory '{rootPath}' doesn't exist");
        RootPath = rootPath;
    }

    // ── Shared file collection logic ──

    public virtual void CollectFiles(bool skipStreamingAssets = false)
    {
        if (GameDataPath != null)
        {
            CollectCompressedGameFiles(GameDataPath);
            CollectSerializedFiles(GameDataPath);
            CollectResourceFiles(GameDataPath);
        }
        if (!skipStreamingAssets && StreamingAssetsPath != null && Directory.Exists(StreamingAssetsPath))
            CollectBundlesRecursive(StreamingAssetsPath);
    }

    /// Check if file is a primary engine file (globalgamemanagers, level*, sharedassets*, etc.)
    public static bool IsPrimaryEngineFile(string fileName)
    {
        return fileName == MainDataName
            || fileName == GlobalGameManagersName
            || fileName == GlobalGameManagerAssetsName
            || fileName == ResourcesAssetsName
            || LevelRegex().IsMatch(fileName)
            || SharedAssetsRegex().IsMatch(fileName);
    }

    // ── Collect methods ──

    protected void CollectCompressedGameFiles(string root)
    {
        AddIfExists(root, DataBundleName, FileCategory.Bundle);
        AddIfExists(root, DataPackBundleName, FileCategory.Bundle);
    }

    protected void CollectSerializedFiles(string root)
    {
        AddIfExists(root, GlobalGameManagersName, FileCategory.Asset);
        if (!File.Exists(Path.Combine(root, GlobalGameManagersName)))
            AddIfExists(root, MainDataName, FileCategory.Asset);

        AddIfExists(root, GlobalGameManagerAssetsName, FileCategory.Asset);
        AddIfExists(root, ResourcesAssetsName, FileCategory.Asset);

        // Track base names of split files we've already handled
        var handledSplitBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);

            // Skip individual .split* files — they'll be handled as a group
            if (SplitFileRegex().IsMatch(name))
            {
                // Extract the base name (e.g., "sharedassets0.assets" from "sharedassets0.assets.split5")
                int splitIdx = name.LastIndexOf(".split", StringComparison.OrdinalIgnoreCase);
                string baseName = name[..splitIdx];

                if (handledSplitBases.Add(baseName))
                {
                    // First time seeing this base name — add the combined entry
                    // Use the base path (without .split*) as the canonical path
                    string basePath = Path.Combine(root, baseName);
                    AddFile(basePath, baseName, FileCategory.Asset);
                }
                continue;
            }

            if (LevelRegex().IsMatch(name))
                AddFile(file, name, FileCategory.Asset);
            else if (SharedAssetsRegex().IsMatch(name))
                AddFile(file, name, FileCategory.Asset);
        }
    }

    protected void CollectResourceFiles(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
                AddFile(file, name, FileCategory.Resource);
        }
    }

    protected void CollectBundles(string dir)
    {
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);

            // Fast path: known bundle extensions
            if (name.EndsWith(AssetBundleExtension, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(AlternateBundleExtension, StringComparison.OrdinalIgnoreCase))
            {
                AddFile(file, Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), FileCategory.Bundle);
                continue;
            }

            // Skip files already collected (serialized assets, resources, etc.)
            if (IsPrimaryEngineFile(name)
                || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || SplitFileRegex().IsMatch(name))
                continue;

            // AR approach: detect bundles by reading file header magic bytes.
            // This catches hex-named asset bundles (e.g. "bf98650d5a96e4d548882e012f6e7f80")
            // that Unity APKs use for on-demand asset delivery.
            if (IsBundleByHeader(file))
                AddFile(file, Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), FileCategory.Bundle);
        }
    }

    /// <summary>Check if a file starts with a known bundle signature (UnityFS/UnityRaw/UnityWeb).</summary>
    private static bool IsBundleByHeader(string path)
    {
        try
        {
            // Span<byte> header = stackalloc byte[8];
            // using var fs = File.OpenRead(path);
            // int read = fs.Read(header);
            // if (read < 7) return false;
            // return BundleReader.IsBundleSignature(header);
            return false;
        }
        catch { return false; }
    }

    protected void CollectBundlesRecursive(string dir)
    {
        CollectBundles(dir);
        foreach (string subDir in Directory.EnumerateDirectories(dir))
            CollectBundlesRecursive(subDir);
    }

    /// <summary>
    /// Collect hex-named serialized files from Data/ that aren't covered by
    /// CollectSerializedFiles (which only picks up known names like globalgamemanagers,
    /// sharedassets*, level*). AR loads these on-demand via RequestDependency;
    /// we collect them upfront since we don't have lazy loading.
    /// </summary>
    protected void CollectExtraSerializedFiles(string root)
    {
        // Track names already collected to avoid duplicates
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
            knownNames.Add(Path.GetFileName(f.FullPath));

        int count = 0;
        foreach (string file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);

            // Skip already known files
            if (knownNames.Contains(name)) continue;

            // Skip known non-serialized extensions
            if (name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(AssetBundleExtension, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(AlternateBundleExtension, StringComparison.OrdinalIgnoreCase)
                || SplitFileRegex().IsMatch(name))
                continue;

            // Check if it's a serialized file by reading the header
            if (IsSerializedFileByHeader(file))
            {
                AddFile(file, name, FileCategory.Asset);
                count++;
            }
            // Also check for bundle signature
            else if (IsBundleByHeader(file))
            {
                AddFile(file, name, FileCategory.Bundle);
                count++;
            }
        }

        if (count > 0)
            Pipeline.ConsoleReporter.Debug($"  Discovered {count} extra serialized files in Data/");
    }

    /// <summary>Check if a file starts with a Unity serialized file header.</summary>
    private static bool IsSerializedFileByHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 20) return false;

            Span<byte> header = stackalloc byte[20];
            if (fs.Read(header) < 20) return false;

            // Unity serialized file header (big-endian):
            // bytes 0-3: metadata size
            // bytes 4-7: file size (or 0 for large files)
            // bytes 8-11: version (format version, typically 17-22+)
            // bytes 12-15: data offset
            uint version = (uint)(header[8] << 24 | header[9] << 16 | header[10] << 8 | header[11]);

            // Valid serialized file versions are in range [9, 100]
            // Versions < 9 are ancient, > 100 is unlikely
            return version >= 9 && version <= 100;
        }
        catch { return false; }
    }

    protected void CollectAssemblies(string managedDir)
    {
        if (!Directory.Exists(managedDir)) return;
        foreach (string file in Directory.EnumerateFiles(managedDir, "*.dll"))
            AddFile(file, Path.GetFileName(file), FileCategory.Assembly);
    }

    // ── Helpers ──

    protected void AddFile(string fullPath, string name, FileCategory category)
    {
        Files.Add(new DiscoveredFile(fullPath, name, category));
    }

    protected bool AddIfExists(string dir, string fileName, FileCategory category)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return false;
        AddFile(path, fileName, category);
        return true;
    }

    // ── Regex patterns (from AR) ──

    [GeneratedRegex(@"^level(?:0|[1-9][0-9]*)(?:\.split0)?$", RegexOptions.Compiled)]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"^sharedassets[0-9]+\.assets", RegexOptions.Compiled)]
    private static partial Regex SharedAssetsRegex();

    [GeneratedRegex(@"\.split[0-9]+$", RegexOptions.Compiled)]
    private static partial Regex SplitFileRegex();
}

/// ImportGate — Entry point for the Import phase (Step A).
/// Accepts a file/folder path, detects platform, extracts if needed, discovers all files.
using Rosetta.Extractor.Imports.Enums;
using Rosetta.Extractor.Imports.Platforms;
using Rosetta.Extractor.Imports.Platforms.Android;
using Rosetta.Modules.Extensions;

namespace Rosetta.Extractor.Imports;

/// Result of the import phase — everything needed for the Reader.
public class ImportResult
{
    public string RootPath { get; set; } = "";
    public PlatformType Platform { get; set; }
    public ScriptBackend Backend { get; set; }
    public Architecture SelectedArch { get; set; }
    public List<DiscoveredFile> Files { get; set; } = new();
    public string? GameDataPath { get; set; }
    public string? LibUnityPath { get; set; }
    public string? Il2CppBinaryPath { get; set; }
    public string? MetadataPath { get; set; }
    public string? StreamingAssetsPath { get; set; }

    public void Clear()
    {
        Files?.Clear();
        Files = new List<DiscoveredFile>();
    }
}

public static class ImportGate
{
    /// Import a game from a file (APK/IPA/etc.) or directory.
    /// Handles extraction, platform detection, and file discovery.
    public static ImportResult Import(string inputPath, string tempDir, bool prefer32Bit = false)
    {
        Pipeline.ConsoleReporter.Phase("Import", inputPath);

        // Step 1: If it's an archive, extract it
        string rootPath;
        if (File.Exists(inputPath))
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            Pipeline.ConsoleReporter.Log("Import", $"Archive detected ({ext}), extracting...");
            string extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            bool isNested = ext is ".xapk" or ".apks" or ".apk+";
            rootPath = ZipHandler.Process(inputPath, extractDir, unzipChilds: isNested, overrideAssets: true);
        }
        else if (Directory.Exists(inputPath))
        {
            rootPath = inputPath;
        }
        else
        {
            throw new FileNotFoundException($"Input path not found: {inputPath}");
        }

        // Step 2: Detect platform
        var structure = PlatformDetector.Detect(rootPath, prefer32Bit);
        Pipeline.ConsoleReporter.Log("Import", $"Platform: {structure.Platform}, Backend: {structure.Backend}");

        // Log architecture info for Android
        Architecture selectedArch = Architecture.Unknown;
        if (structure is Rosetta.Extractor.Imports.Platforms.Android.Android android)
        {
            selectedArch = android.SelectedArch;
            Pipeline.ConsoleReporter.Log("Import", $"Architecture: {selectedArch}");
        }

        // Step 3: Collect files
        structure.CollectFiles();

        // Log discovered files as single line
        var grouped = structure.Files.GroupBy(f => f.Category).OrderBy(g => g.Key);
        var parts = grouped.Select(g => $"{g.Count()} {g.Key.ToString().ToLower()}");
        Pipeline.ConsoleReporter.Log("Import", string.Join(", ", parts));

        return new ImportResult
        {
            RootPath = rootPath,
            Platform = structure.Platform,
            Backend = structure.Backend,
            SelectedArch = selectedArch,
            Files = structure.Files,
            GameDataPath = structure.GameDataPath,
            LibUnityPath = structure.UnityPlayerPath,
            Il2CppBinaryPath = structure.Il2CppBinaryPath,
            MetadataPath = structure.MetadataPath,
            StreamingAssetsPath = structure.StreamingAssetsPath
        };
    }
}

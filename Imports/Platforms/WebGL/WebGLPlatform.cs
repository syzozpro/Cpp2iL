/// WebGL — WebGL game structure detection and file collection.
/// Detects Build/ or Development/ or Release/ subdirectories with .data or .data.unityweb files.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.WebGL;

public sealed class WebGLPlatform : PlatformBase
{
    public override PlatformType Platform => PlatformType.WebGL;

    const string BuildName = "Build";
    const string DevelopmentName = "Development";
    const string ReleaseName = "Release";
    const string HtmlExtension = ".html";
    const string DataExtension = ".data";
    const string DataGzExtension = ".datagz";
    const string DataWebExtension = ".data.unityweb";
    const string DataBrExtension = ".data.br";

    /// Path to the main .data file (may be compressed).
    public string? DataFilePath { get; private set; }

    public WebGLPlatform(string rootPath) : base(rootPath)
    {
        Name = Path.GetFileName(rootPath);
        GameDataPath = rootPath;
        Backend = ScriptBackend.IL2Cpp; // WebGL is always IL2CPP

        // Discover the data file
        DataFilePath = FindDataFile(rootPath);
        if (DataFilePath == null)
            throw new FileNotFoundException("No WebGL data file (.data, .data.unityweb, .datagz, .data.br) found");
    }

    public override void CollectFiles(bool skipStreamingAssets = false)
    {
        // For WebGL, the main data file is a combined bundle containing everything
        if (DataFilePath != null)
        {
            string name = Path.GetFileName(DataFilePath);
            // Determine category based on extension
            if (name.EndsWith(DataWebExtension, StringComparison.Ordinal)
                || name.EndsWith(DataGzExtension, StringComparison.Ordinal)
                || name.EndsWith(DataBrExtension, StringComparison.Ordinal))
            {
                AddFile(DataFilePath, name, FileCategory.Bundle);
            }
            else
            {
                AddFile(DataFilePath, name, FileCategory.Bundle);
            }
        }

        // Also collect any streaming assets or bundles in the directory
        string buildDir = Path.Combine(RootPath, BuildName);
        if (Directory.Exists(buildDir))
        {
            foreach (string file in Directory.EnumerateFiles(buildDir))
            {
                string fname = Path.GetFileName(file);
                if (file == DataFilePath) continue; // already added
                if (fname.EndsWith(".framework.js", StringComparison.OrdinalIgnoreCase)
                    || fname.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                    || fname.EndsWith(".wasm.br", StringComparison.OrdinalIgnoreCase)
                    || fname.EndsWith(".wasm.gz", StringComparison.OrdinalIgnoreCase))
                {
                    AddFile(file, fname, FileCategory.Lib);
                }
            }
        }
    }

    /// Check if a directory has a WebGL game structure.
    public static bool IsWebGLStructure(string path)
    {
        if (!Directory.Exists(path)) return false;

        // Must have at least one .html file and a Build/ or Development/ or Release/ dir
        bool hasHtml = Directory.EnumerateFiles(path)
            .Any(f => f.EndsWith(HtmlExtension, StringComparison.Ordinal));
        if (!hasHtml) return false;

        return FindDataFile(path) != null;
    }

    /// Search for the main data file across all possible WebGL directory layouts.
    private static string? FindDataFile(string rootPath)
    {
        // Modern: Build/GameName.data.unityweb or Build/GameName.data.br or Build/GameName.data
        string buildDir = Path.Combine(rootPath, BuildName);
        if (Directory.Exists(buildDir))
        {
            string? found = SearchForData(buildDir, DataWebExtension)
                         ?? SearchForData(buildDir, DataBrExtension)
                         ?? SearchForData(buildDir, DataExtension);
            if (found != null) return found;
        }

        // Development: Development/GameName.data
        string devDir = Path.Combine(rootPath, DevelopmentName);
        if (Directory.Exists(devDir))
        {
            string? found = SearchForData(devDir, DataExtension);
            if (found != null) return found;
        }

        // Release: Release/GameName.datagz
        string relDir = Path.Combine(rootPath, ReleaseName);
        if (Directory.Exists(relDir))
        {
            string? found = SearchForData(relDir, DataGzExtension);
            if (found != null) return found;
        }

        return null;
    }

    private static string? SearchForData(string dir, string extension)
    {
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            if (file.EndsWith(extension, StringComparison.Ordinal))
                return file;
        }
        return null;
    }
}

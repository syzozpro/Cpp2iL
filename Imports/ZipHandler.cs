/// ZipHandler — Extract ZIP-based game archives (APK, XAPK, IPA, OBB, etc.)
using System.IO.Compression;

namespace Rosetta.Modules.Extensions;

public static class ZipHandler
{
    // Supported extensions (from AR ZipExtractor)
    static readonly HashSet<string> SingleZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".apk", ".obb", ".vpk", ".ipa", ".xap", ".appx"
    };
    static readonly HashSet<string> NestedZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xapk", ".apks", ".apk+"
    };

    // ZIP magic bytes
    const uint ZipNormalMagic  = 0x04034B50;
    const uint ZipEmptyMagic   = 0x06054B50;
    const uint ZipSpannedMagic = 0x08074B50;

    /// Process a path — if it's a supported archive, extract it.
    /// Returns the path to the extracted directory (or the original path if not an archive).
    public static string Process(string path, string outputDir, bool unzipChilds = false, bool overrideAssets = false)
    {
        if (!File.Exists(path)) return path;

        string ext = Path.GetExtension(path);

        if (NestedZipExtensions.Contains(ext))
            return ExtractNested(path, outputDir, overrideAssets);

        if (SingleZipExtensions.Contains(ext))
            return ExtractSingle(path, outputDir, overrideAssets);

        return path;
    }

    /// Extract a single ZIP archive (APK, OBB, IPA, etc.)
    public static string ExtractSingle(string zipPath, string outputDir, bool overrideAssets = false)
    {
        if (!HasCompatibleMagic(zipPath)) return zipPath;
        DecompressArchive(zipPath, outputDir, overrideAssets);
        return outputDir;
    }

    /// Extract a nested archive (XAPK, APKS) — first extract outer, then inner APKs.
    public static string ExtractNested(string xapkPath, string outputDir, bool overrideAssets = false)
    {
        if (!HasCompatibleMagic(xapkPath)) return xapkPath;

        // Step 1: Extract outer archive to intermediate dir
        string intermediateDir = Path.Combine(outputDir, "_xapk_intermediate");
        Directory.CreateDirectory(intermediateDir);
        DecompressArchive(xapkPath, intermediateDir, overrideAssets);

        // Step 2: Extract each inner APK into output dir
        foreach (string file in Directory.EnumerateFiles(intermediateDir))
        {
            if (Path.GetExtension(file).Equals(".apk", StringComparison.OrdinalIgnoreCase))
                DecompressArchive(file, outputDir, overrideAssets);
        }

        // Clean intermediate
        try { Directory.Delete(intermediateDir, true); } catch { }

        return outputDir;
    }

    /// Decompress a ZIP archive to the output directory.
    static void DecompressArchive(string zipPath, string outputDir, bool overrideAssets)
    {
        Directory.CreateDirectory(outputDir);
        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        string fullOutputDir = Path.GetFullPath(outputDir);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // Skip directory entries

            string destPath = Path.GetFullPath(Path.Combine(fullOutputDir, entry.FullName));

            // Security: prevent path traversal
            if (!destPath.StartsWith(fullOutputDir, StringComparison.Ordinal))
                continue;

            // Skip if exists and not overriding
            if (!overrideAssets && File.Exists(destPath))
                continue;

            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destPath);
            entryStream.CopyTo(fileStream);
        }
    }

    /// Check if file has a valid ZIP magic number.
    static bool HasCompatibleMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[4];
            if (stream.Read(buf) < 4) return false;
            uint magic = BitConverter.ToUInt32(buf);
            return magic == ZipNormalMagic || magic == ZipEmptyMagic || magic == ZipSpannedMagic;
        }
        catch { return false; }
    }
}

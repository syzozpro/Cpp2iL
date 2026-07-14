/// NativeLibNames — Shared native library file names per platform.
/// Not hardcoded in platform classes — centralized here for all platforms.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms.Android;

/// Represents a discovered native library with its path and architecture.
public record NativeLib(string FullPath, string FileName, Architecture Arch);

/// Shared native library file names and arch folder mappings.
public static class NativeLibNames
{
    // ── IL2Cpp binaries ──
    public const string Il2CppAndroid = "libil2cpp.so";
    public const string Il2CppWindows = "GameAssembly.dll";
    public const string Il2CppMac     = "GameAssembly.dylib";
    public const string Il2CppLinux   = "GameAssembly.so";
    public const string Il2CppiOS     = "UnityFramework"; // Inside .app

    // ── Unity player / engine ──
    public const string UnityAndroid = "libunity.so";
    public const string UnityWindows = "UnityPlayer.dll";
    public const string UnityLinux   = "UnityPlayer.so";

    // ── Android arch folder names → Architecture mapping ──
    static readonly Dictionary<string, Architecture> AndroidArchMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arm64-v8a"]    = Architecture.Arm64,
        ["armeabi-v7a"]  = Architecture.Arm,
        ["x86_64"]       = Architecture.X86_64,
        ["x86"]          = Architecture.X86,
    };

    // ── Preferred architecture order (64-bit first) ──
    static readonly Architecture[] PreferredOrder =
    [
        Architecture.Arm64,
        Architecture.X86_64,
        Architecture.Arm,
        Architecture.X86,
    ];

    // ── Preferred architecture order (32-bit first) ──
    static readonly Architecture[] PreferredOrder32 =
    [
        Architecture.Arm,
        Architecture.X86,
        Architecture.Arm64,
        Architecture.X86_64,
    ];

    /// Discover all native libraries matching a filename under a lib root directory.
    /// Returns all found copies across all architectures.
    public static List<NativeLib> DiscoverAll(string libRoot, string fileName)
    {
        var results = new List<NativeLib>();
        if (!Directory.Exists(libRoot)) return results;

        foreach (string file in Directory.EnumerateFiles(libRoot, fileName, SearchOption.AllDirectories))
        {
            string? parentDir = Path.GetDirectoryName(file);
            string parentName = parentDir != null ? Path.GetFileName(parentDir) : "";
            Architecture arch = AndroidArchMap.GetValueOrDefault(parentName, Architecture.Unknown);
            results.Add(new NativeLib(file, fileName, arch));
        }
        return results;
    }

    /// Pick the best (preferred) library from a list of discovered libs.
    /// Prefers 64-bit over 32-bit by default, unless prefer32Bit is true.
    public static NativeLib? PickBest(List<NativeLib> libs, bool prefer32Bit = false)
    {
        if (libs.Count == 0) return null;
        var preferredOrder = prefer32Bit ? PreferredOrder32 : PreferredOrder;
        foreach (var arch in preferredOrder)
        {
            var match = libs.Find(l => l.Arch == arch);
            if (match != null) return match;
        }
        return libs[0]; // fallback to first found
    }
}

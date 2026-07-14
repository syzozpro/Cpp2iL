/// PlatformDetector — Auto-detects platform from extracted directory paths.
/// Checks platforms in order of specificity (most specific first).
using Rosetta.Extractor.Imports.Enums;
using Rosetta.Extractor.Imports.Platforms.iOS;
using Rosetta.Extractor.Imports.Platforms.Linux;
using Rosetta.Extractor.Imports.Platforms.Mac;
using Rosetta.Extractor.Imports.Platforms.WebGL;
using Rosetta.Extractor.Imports.Platforms.Windows;
using AndroidPlatform = Rosetta.Extractor.Imports.Platforms.Android.Android;

namespace Rosetta.Extractor.Imports.Platforms;

public static class PlatformDetector
{
    /// Detect the platform and return the appropriate structure handler.
    /// Checks platforms in order of specificity (most specific first).
    public static IPlatformStructure Detect(string rootPath, bool prefer32Bit = false)
    {
        // Android: assets/bin/Data exists (APK/OBB)
        if (AndroidPlatform.IsAndroidStructure(rootPath))
            return new AndroidPlatform(rootPath, prefer32Bit: prefer32Bit);

        // iOS: Payload/GameName.app/Data exists (extracted IPA)
        if (iOSPlatform.IsIOSStructure(rootPath))
            return new iOSPlatform(rootPath);

        // Mac: GameName.app/Contents/Resources/Data exists
        if (MacPlatform.IsMacStructure(rootPath))
            return new MacPlatform(rootPath);

        // WebGL: index.html + Build/*.data.unityweb
        if (WebGLPlatform.IsWebGLStructure(rootPath))
            return new WebGLPlatform(rootPath);

        // Linux: GameName.x86_64 + GameName_Data/  (check before Windows to avoid false positive on .exe-less dirs)
        if (LinuxPlatform.IsLinuxStructure(rootPath))
            return new LinuxPlatform(rootPath);

        // Windows: GameName.exe + GameName_Data/
        if (WindowsPlatform.IsWindowsStructure(rootPath))
            return new WindowsPlatform(rootPath);

        // Fallback: Mixed — collect everything found
        return new Mixed(rootPath);
    }

    /// Detect platform type without constructing the full structure.
    public static PlatformType DetectType(string rootPath)
    {
        if (AndroidPlatform.IsAndroidStructure(rootPath)) return PlatformType.Android;
        if (iOSPlatform.IsIOSStructure(rootPath)) return PlatformType.iOS;
        if (MacPlatform.IsMacStructure(rootPath)) return PlatformType.Mac;
        if (WebGLPlatform.IsWebGLStructure(rootPath)) return PlatformType.WebGL;
        if (LinuxPlatform.IsLinuxStructure(rootPath)) return PlatformType.Linux;
        if (WindowsPlatform.IsWindowsStructure(rootPath)) return PlatformType.Windows;
        return PlatformType.Mixed;
    }
}

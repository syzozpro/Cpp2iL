/// Mixed — Fallback platform structure for unrecognized game directories.
/// Collects all serialized files and bundles found at root level.
using Rosetta.Extractor.Imports.Enums;

namespace Rosetta.Extractor.Imports.Platforms;

public sealed class Mixed : PlatformBase
{
    public override PlatformType Platform => PlatformType.Mixed;

    public Mixed(string rootPath) : base(rootPath)
    {
        GameDataPath = rootPath;
    }

    public override void CollectFiles(bool skipStreamingAssets = false)
    {
        if (GameDataPath == null) return;
        CollectCompressedGameFiles(GameDataPath);
        CollectSerializedFiles(GameDataPath);
        CollectResourceFiles(GameDataPath);
        CollectBundlesRecursive(GameDataPath);
    }
}

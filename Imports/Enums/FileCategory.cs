/// FileCategory — Categorizes files discovered during platform import.
namespace Rosetta.Extractor.Imports.Enums;

public enum FileCategory
{
    /// Serialized asset file (globalgamemanagers, level*, sharedassets*, etc.)
    Asset,
    /// Unity bundle file (.unity3d, .bundle)
    Bundle,
    /// Resource/streaming data file (.resource, .resS)
    Resource,
    /// Native library (libunity.so, libil2cpp.so, GameAssembly.dll, UnityPlayer.dll)
    Lib,
    /// IL2CPP metadata (global-metadata.dat)
    Metadata,
    /// Managed assembly (.dll in Managed/)
    Assembly,
    /// StreamingAssets content
    StreamingAsset,
    /// Configuration or manifest file
    Config,
    /// Unknown / not classified
    Unknown,
}

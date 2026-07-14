/// Architecture — CPU architecture of native libraries.
namespace Rosetta.Extractor.Imports.Enums;

public enum Architecture
{
    Arm,       // armeabi-v7a (32-bit)
    Arm64,     // arm64-v8a (64-bit)
    X86,       // x86 (32-bit)
    X86_64,    // x86_64 (64-bit)
    Unknown,
}

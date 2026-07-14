namespace Rosetta.Analysis.IR;

/// <summary>
/// Version-aware configuration for IL2CPP runtime constants.
/// These values change based on the Unity/IL2CPP version used to compile the game.
/// Injected into the noise reducer to avoid hardcoded magic numbers.
/// </summary>
public sealed class Il2CppEnvironmentConfig
{
    /// <summary>Offset into Il2CppClass for the class initialization flag (initialized != 0).</summary>
    public long ClassInitOffset { get; init; } = 0xE4;

    /// <summary>ADRP base address for the metadata initialization flag table.</summary>
    public long MetadataInitFlagBase { get; init; } = 0x255B000;

    /// <summary>Minimum offset for metadata init flag reads (addr + 0x800..0x8FF).</summary>
    public long MetadataFlagOffsetMin { get; init; } = 0x800;

    /// <summary>Maximum offset for metadata init flag reads.</summary>
    public long MetadataFlagOffsetMax { get; init; } = 0x8FF;

    /// <summary>GP callee-saved register range (inclusive).</summary>
    public int CalleeSavedGpMin { get; init; } = 19;
    public int CalleeSavedGpMax { get; init; } = 30;

    /// <summary>FP callee-saved register range (inclusive).</summary>
    public int CalleeSavedFpMin { get; init; } = 8;
    public int CalleeSavedFpMax { get; init; } = 15;

    /// <summary>
    /// Known IL2CPP runtime helper/noise call name fragments.
    /// Any call whose annotation contains one of these is classified as a noise sink.
    /// </summary>
    public string[] RuntimeHelperNames { get; init; } =
    [
        "il2cpp_runtime_helper",
        "il2cpp_codegen_initialize_runtime_metadata",
        "il2cpp_codegen_runtime_class_init",
        "class_init_inline",
        "WriteBarrier",
        "NullCheck",
        "il2cpp_codegen_raise",
        "ThrowNullReferenceException",
    ];

    /// <summary>Create config for a specific Unity version.</summary>
    public static Il2CppEnvironmentConfig ForVersion(string unityVersion)
    {
        if (unityVersion.StartsWith("2019"))
            return new Il2CppEnvironmentConfig { ClassInitOffset = 0xB8 };

        // Unity 2020+, 2021+, 2022+, Unity 6 (6000.x) all use modern layout
        return new Il2CppEnvironmentConfig();
    }

    /// <summary>Default config for modern Unity (2020+).</summary>
    public static Il2CppEnvironmentConfig Default => new();
}

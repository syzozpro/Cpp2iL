namespace Rosetta.Config;

/// <summary>
/// Configuration options for the IL2CPP decompilation pipeline.
/// Passed into the pipeline context and read by stages.
/// </summary>
public sealed class Il2cppConfig
{
    public enum CSharpVersion { CSharp8, CSharp9, CSharp10, CSharp11, CSharp12 }
    public enum TargetAssemblyMode { All, AssemblyCSharp, MainAssemblies }
    public enum DecoderMode { Rosetta, Capstone, Disarm }


    public static bool IsDebugMode { get; set; } = false;
    public static bool DisableDeadCodeEliminator {get; set; } = false;
    public static bool DisableBoilerplatePruner {get; set; } = false || IsDebugMode;
    public static bool DisableFieldRemoveCTR { get; set; } = false || IsDebugMode;
    public static bool DisableLoopReconstructure { get; set; } = false || IsDebugMode;
    public static bool DisableNamingStyle { get; set; } = false || IsDebugMode;
    public static bool RestylingCode { get; set; } = true;
    public static bool EnableIEnumeratorMapping { get; set; } = true;
    public static bool EnableEventsActionsMapping { get; set; } = true;

    public static bool SkipMethodBodyAnalysis { get; set; } = false;
    public static bool DumpIr { get; set; } = false;
    public string OutputDirectory { get; set; } = "/Users/youness/Downloads/GameRipper/Decompiled Game";
    public string? TargetAssembly { get; set; }
    public static string? MetadataDecryptionKey { get; set; }

    public static TargetAssemblyMode TargetAssemblies { get; set; } = TargetAssemblyMode.All;
    public static DecoderMode Decoder { get; set; } = DecoderMode.Rosetta;
    public static CSharpVersion OutputLanguageVersion { get; set; } = CSharpVersion.CSharp10;

    public static int MaxParallelism { get; set; } = 0;
}

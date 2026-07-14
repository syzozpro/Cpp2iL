// AssemblyClassifier — Determines which assemblies to decompile vs skip.
//
// That dictionary contains three categories of "skip" assemblies:
//   1. Unity engine modules  (assemblyData.Unity)     — e.g. UnityEngine.CoreModule
//   2. Mono/.NET BCL         (assemblyData.Mono2/Mono4) — e.g. mscorlib, System.*
//   3. Unity extensions      (assemblyData.UnityExtensions) — e.g. UnityEngine.AccessibilityModule
//
// We replicate this classification using name-pattern matching, which covers
// all Unity versions without requiring an external data file.

using System;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Classifies assemblies to determine if they should be decompiled or skipped.
/// </summary>
public enum AssemblyExportType
{
    Predefined = 0,
    MonoBclAssembly = 1,
    UnityEditorAssembly = 2,
    UnityInternalAssembly = 3,
    UnityEngineAssembly = 4,
    Decompile,
}

public static class AssemblyClassifier
{
    // ════════════════════════════════════════════════════════════════════
    // These are Unity's "predefined" game assemblies — always decompile.
    // ════════════════════════════════════════════════════════════════════

    public static bool IsPredefinedAssembly(string assemblyName)
    {
        return assemblyName
            is "Assembly-CSharp"
            or "Assembly-CSharp-firstpass"
            or "Assembly-CSharp-Editor"
            or "Assembly-CSharp-Editor-firstpass"
            or "Assembly-UnityScript"
            or "Assembly-UnityScript-firstpass";
    }

    // ════════════════════════════════════════════════════════════════════
    //   if (ReferenceAssemblyDictionary.ContainsKey(name)) → Skip
    //   else → Decompile
    //
    // The ReferenceAssemblyDictionary is built from:
    //   - assemblyData.UnityExtensions  (Unity extension modules with GUIDs)
    //   - assemblyData.Mono2/Mono4      (.NET/Mono framework assemblies)
    //   - assemblyData.Unity            (Unity engine assemblies)
    //   - Hard-coded: "UnityEngine"
    //
    // We classify using the same categories via name patterns.
    // ════════════════════════════════════════════════════════════════════

    public static AssemblyExportType Classify(string assemblyName)
    {
        // Predefined game assemblies → always decompile
        if (IsPredefinedAssembly(assemblyName))
            return AssemblyExportType.Decompile;

        // Check if it's a reference (framework) assembly that should be skipped
        if (IsReferenceAssembly(assemblyName, out AssemblyExportType type))
            return type;

        // Everything else is user/third-party code → decompile
        return AssemblyExportType.Decompile;
    }

    // ────────────────────────────────────────────────────────────────────
    // Reference assembly detection — replaces assemblyData lookup
    // ────────────────────────────────────────────────────────────────────

    private static bool IsReferenceAssembly(string name, out AssemblyExportType type)
    {
        type = AssemblyExportType.Decompile;

        bool a = IsUnityEngineAssembly(name);
        bool b = IsMonoBclAssembly(name);
        bool c = IsUnityEditorAssembly(name);
        bool d = IsUnityInternalAssembly(name);
        
        if(a) type = AssemblyExportType.UnityEngineAssembly;
        if(b) type = AssemblyExportType.MonoBclAssembly;
        if(c) type = AssemblyExportType.UnityEditorAssembly;
        if(d) type = AssemblyExportType.UnityInternalAssembly;

        return a || b || c || d;
    }

    /// <summary>
    /// Matches assemblyData.Unity + assemblyData.UnityExtensions + "UnityEngine" hard-code.
    /// Unity engine modules follow the pattern: UnityEngine.{Name}Module
    /// The bare "UnityEngine" assembly is a thin forwarder.
    /// </summary>
    private static bool IsUnityEngineAssembly(string name)
    {
        if (name == "UnityEngine")
            return true;

        // Unity engine modules: UnityEngine.CoreModule, UnityEngine.PhysicsModule, etc.
        // The "Module" suffix distinguishes engine assemblies from packages.
        // e.g. "UnityEngine.UI" is a PACKAGE (decompile), not a module (skip).
        if (name.StartsWith("UnityEngine.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Matches assemblyData.Mono2/Mono4 — the .NET/Mono BCL assemblies that ship
    /// with Unity's Mono runtime. These are framework code, not game code.
    ///
    /// NuGet packages like System.Runtime.CompilerServices.Unsafe are NOT in this
    /// list — they have deeply qualified names (4+ parts) and should be decompiled.
    /// </summary>
    private static bool IsMonoBclAssembly(string name)
    {
        // Core .NET runtime
        if (name is "mscorlib" or "netstandard")
            return true;

        // Mono-specific assemblies
        if (name.StartsWith("Mono.", StringComparison.Ordinal))
            return true;

        // System assemblies: "System" or "System.{ShortName}" (1 level deep)
        // but NOT "System.Runtime.CompilerServices.Unsafe" (NuGet packages have 3+ dots)
        if (name == "System")
            return true;

        if (name.StartsWith("System.", StringComparison.Ordinal))
        {
            // Count dots: System.Core (1 dot) → BCL, System.Runtime.CompilerServices.Unsafe (3 dots) → NuGet
            // System.Configuration, System.Numerics, System.Net.Http, System.Xml.Linq,
            // System.Runtime.Serialization, System.ServiceModel.Internals, System.Transactions
            // Max dots in BCL = 2 (System.Runtime.Serialization, System.Xml.Linq, System.Net.Http,
            // System.ServiceModel.Internals)
            // NuGet packages = 3+ dots (System.Runtime.CompilerServices.Unsafe)
            int dotCount = 0;
            for (int i = 0; i < name.Length; i++)
                if (name[i] == '.') dotCount++;

            return dotCount <= 2;
        }

        return false;
    }

    /// <summary>
    /// Unity Editor assemblies — shouldn't appear in IL2CPP builds but included for safety.
    /// </summary>
    private static bool IsUnityEditorAssembly(string name)
    {
        return name.StartsWith("UnityEditor", StringComparison.Ordinal);
    }

    /// <summary>
    /// Unity runtime internal assemblies — not game code, not packages.
    /// Unity.Scripting: internal scripting runtime (lifecycle, subsystems).
    /// __Generated: IL2CPP-generated metadata types (enum placeholders, etc.).
    /// </summary>
    private static bool IsUnityInternalAssembly(string name)
    {
        return name is "Unity.Scripting" or "__Generated";
    }
}

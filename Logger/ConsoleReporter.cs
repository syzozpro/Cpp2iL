using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Rosetta.Binary;

namespace Rosetta.Pipeline;

public static class ConsoleReporter
{
    /// <summary>When true, emit high-detail verbose trace logs for debugging.</summary>
    public static bool Verbose { get; set; } = false;

    /// <summary>
    /// Fast-path check for hot-path callers to skip string interpolation entirely.
    /// Use: if (ConsoleReporter.IsTracing) ConsoleReporter.Trace(...);
    /// </summary>
    public static bool IsTracing => Verbose;

    /// <summary>
    /// The current target being processed (e.g. class name).
    /// Used by 'gen:TargetName' verbose filters.
    /// </summary>
    private static System.Threading.AsyncLocal<string?> _activeTarget = new();

    public static string? ActiveTarget 
    {
        get => _activeTarget.Value;
        set => _activeTarget.Value = value;
    }

    /// <summary>
    /// The current method being processed (e.g. method name).
    /// Used by 'fen:MethodName' verbose filters.
    /// </summary>
    private static System.Threading.AsyncLocal<string?> _activeMethod = new();

    public static string? ActiveMethod
    {
        get => _activeMethod.Value;
        set => _activeMethod.Value = value;
    }


    /// <summary>The current pipeline phase label (e.g., "Import", "Reader", "Processing", "Export").</summary>
    private static string _currentCategory = "General";
    private const int CategoryWidth = 16;

    /// <summary>Set the active category for subsequent log calls.</summary>
    public static void SetCategory(string category) => _currentCategory = category;

    public static event Action<string, string>? OnLogEvent;

    /// <summary>Write a category:message line.</summary>
    public static void Log(string category, string message)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{category.PadRight(CategoryWidth)}: ");
        Console.ResetColor();
        Console.WriteLine(message);
        OnLogEvent?.Invoke(category, message);
    }

    /// <summary>Write using the current category.</summary>
    public static void Log(string message) => Log(_currentCategory, message);

    // ── Phase markers ───────────────────────────────────────────────────

    public static void Phase(string tag, string msg)
    {
        _currentCategory = tag;
        Log(tag, msg);
    }

    public static void SubPhase(string msg)
    {
        Log(_currentCategory, msg);
    }

    // ── Level-based output ──────────────────────────────────────────────

    public static void Info(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (Verbose && !ShouldLog(callerPath, callerMember)) return;

        string className = Path.GetFileNameWithoutExtension(callerPath);
        string prefix = $"{className}:{callerMember} : ";
        // Strip leading whitespace/bullets for clean category output
        string clean = msg.TrimStart();
        Log(_currentCategory, prefix + clean);
    }

    public static void Success(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (Verbose && !ShouldLog(callerPath, callerMember)) return;

        string className = Path.GetFileNameWithoutExtension(callerPath);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{className}:{callerMember} : ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(msg.TrimStart());
        Console.ResetColor();
    }

    public static void Warning(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (Verbose && !ShouldLog(callerPath, callerMember)) return;

        string className = Path.GetFileNameWithoutExtension(callerPath);
        string prefix = ActiveMethod != null ? $"[{ActiveMethod}] " : "";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{className}:{callerMember} : ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(prefix + msg.TrimStart());
        Console.ResetColor();
    }

    public static void Error(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (Verbose && !ShouldLog(callerPath, callerMember)) return;

        string className = Path.GetFileNameWithoutExtension(callerPath);
        string prefix = ActiveMethod != null ? $"[{ActiveMethod}] " : "";
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"{className}:{callerMember} : ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(prefix + msg.TrimStart());
        Console.ResetColor();
    }


    // ── Verbose/debug (filter-aware) ────────────────────────────────────

    // Filters for class-level or function-level debugging
    private static List<(string ClassName, string? FunctionName, string? TargetName, string? ActiveMethodName)> _verboseFilters = new();

    public static void SetVerboseFilters(string filterString)
    {
        _verboseFilters.Clear();
        string[] filters = filterString.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var filter in filters)
        {
            string f = filter.Trim();
            
            if (f.StartsWith("gen:", StringComparison.OrdinalIgnoreCase))
            {
                string target = f.Substring(4);
                string? method = null;
                int hashIdx = target.IndexOf("##");
                if (hashIdx >= 0)
                {
                    method = target.Substring(hashIdx + 2);
                    target = target.Substring(0, hashIdx);
                }
                _verboseFilters.Add((string.Empty, null, target, method));
                continue;
            }

            if (f.StartsWith("fen:", StringComparison.OrdinalIgnoreCase))
            {
                _verboseFilters.Add((string.Empty, null, null, f.Substring(4)));
                continue;
            }

            string className;
            string? functionName = null;

            int hashIdx2 = f.IndexOf("##");
            if (hashIdx2 >= 0)
            {
                className = f.Substring(0, hashIdx2);
                functionName = f.Substring(hashIdx2 + 2);
            }
            else
            {
                className = f;
            }

            // Remove .cs if user included it
            if (className.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                className = className.Substring(0, className.Length - 3);
            }

            _verboseFilters.Add((className, functionName, null, null));
        }
    }

    private static bool ShouldLog(string callerPath, string callerMember)
    {
        if (!Verbose) return false;
        if (_verboseFilters.Count == 0) return true; // Global verbose

        string className = Path.GetFileNameWithoutExtension(callerPath);

        foreach (var filter in _verboseFilters)
        {
            // Target-based filter
            if (filter.TargetName != null)
            {
                if (ActiveTarget != null && ActiveTarget.Contains(filter.TargetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (filter.ActiveMethodName != null)
                    {
                        if (ActiveMethod != null && ActiveMethod.Contains(filter.ActiveMethodName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        return true;
                    }
                }
                continue;
            }

            // Active Method filter
            if (filter.ActiveMethodName != null)
            {
                if (ActiveMethod != null && ActiveMethod.Contains(filter.ActiveMethodName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                continue;
            }

            // Class/Member-based filter
            if (string.Equals(filter.ClassName, className, StringComparison.OrdinalIgnoreCase))
            {
                if (filter.FunctionName == null) return true; // Whole class
                if (string.Equals(filter.FunctionName, callerMember, StringComparison.OrdinalIgnoreCase)) return true; // Specific function
            }
        }
        return false;
    }

    /// <summary>Verbose trace log — only emitted when Verbose is enabled.
    /// Use for function entry/exit, loop iterations, decision branches.</summary>
    public static void Trace(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (!ShouldLog(callerPath, callerMember)) return;
        string className = Path.GetFileNameWithoutExtension(callerPath);
        string prefix = ActiveMethod != null ? $"[{ActiveMethod}] " : "";
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{className}:{callerMember} : ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(prefix + msg.TrimStart());
        Console.ResetColor();
    }

    public static void Debug(string msg, [CallerFilePath] string callerPath = "", [CallerMemberName] string callerMember = "")
    {
        if (!ShouldLog(callerPath, callerMember)) return;
        string className = Path.GetFileNameWithoutExtension(callerPath);
        string prefix = ActiveMethod != null ? $"[{ActiveMethod}] " : "";
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"{className}:{callerMember} : ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine(prefix + msg.TrimStart());
        Console.ResetColor();
    }

    // ── Progress bar ────────────────────────────────────────────────────

    public static event Action<int, int, string>? OnProgressEvent;

    /// <summary>Render an inline progress bar aligned with log output.</summary>
    public static void ProgressBar(int current, int total, string fileName)
    {
        OnProgressEvent?.Invoke(current, total, fileName);

        const int barWidth = 20;
        double ratio = total > 0 ? (double)current / total : 1.0;
        int filled = (int)(ratio * barWidth);
        int empty = barWidth - filled;

        string bar = new string('━', filled) + new string('─', empty);
        double pct = ratio * 100;

        // Truncate filename to keep line short
        string shortName = fileName.Length > 50 ? fileName[..50] + "…" : fileName;

        // Category-aligned format:  Export          : [━━━━━─────] 65% Texture2D
        Console.Write('\r');
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{"Export".PadRight(CategoryWidth)}: ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(bar[..filled]);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(bar[filled..]);
        Console.ResetColor();
        Console.Write($" {pct,3:F0}% {shortName}");

        // Pad to clear leftover chars
        try
        {
            int totalWritten = CategoryWidth + 2 + barWidth + 5 + shortName.Length;
            int pad = Math.Max(0, Console.WindowWidth - totalWritten - 1);
            if (pad > 0) Console.Write(new string(' ', pad));
        }
        catch { Console.Write("     "); }
    }

    /// <summary>Clear the progress bar line and move to next line.</summary>
    public static void EndProgress()
    {
        try
        {
            Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
        }
        catch
        {
            Console.Write("\r                                                                                    \r");
        }
    }

    // ── Binary analysis helpers ─────────────────────────────────────────

    public static void PrintSection(string name, BinarySectionHeader? section)
    {
        if (section.HasValue)
        {
            var s = section.Value;
            Log("Binary", $"{name,-12} VA=0x{s.VirtualAddr:X12}  Offset=0x{s.FileOffset:X8}  Size={s.Size,12:N0}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Log("Binary", $"{name,-12} NOT FOUND");
            Console.ResetColor();
        }
    }

    public static string SectionName(int index) => index switch
    {
        0  => "StringLiteral Offsets",
        1  => "StringLiteral Data",
        2  => "MetadataStrings",
        3  => "Events",
        4  => "Properties",
        5  => "Methods",
        6  => "ParamDefaultValues",
        7  => "FieldDefaultValues",
        8  => "DefaultValuesData",
        9  => "FieldMarshaledSizes",
        10 => "Parameters",
        11 => "Fields",
        12 => "GenericParameters",
        13 => "GenericConstraints",
        14 => "GenericContainers",
        15 => "NestedTypes",
        16 => "Interfaces",
        17 => "VTable",
        18 => "InterfaceOffsets",
        19 => "TypeDefinitions",
        20 => "Images",
        21 => "Assemblies",
        22 => "FieldRefs",
        23 => "ReferencedAssemblies",
        24 => "AttributeData",
        25 => "AttributeDataRanges",
        26 => "UnresolvedVC_ParamTypes",
        27 => "UnresolvedVC_ParamRanges",
        28 => "WinRT_TypeNamePairs",
        29 => "WinRT_Strings",
        30 => "ExportedTypes",
        _  => $"Unknown_{index}",
    };
}

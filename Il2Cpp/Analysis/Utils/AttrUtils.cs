using System;
using System.Collections.Generic;
using System.Text;

namespace Rosetta.Analysis.Utils;

public static class AttrUtils
{
    private static bool IsBlockStarter(string attr)
    {
        return attr.StartsWith("[Header") || attr.StartsWith("[Space");
    }

    private static bool IsStandalone(string attr)
    {
        return attr.StartsWith("[Obsolete") ||
               attr.StartsWith("[RequireComponent") ||
               attr.StartsWith("[ExecuteInEditMode") ||
               attr.StartsWith("[ExecuteAlways") ||
               attr.StartsWith("[AddComponentMenu") ||
               attr.StartsWith("[SelectionBase") ||
               attr.StartsWith("[DisallowMultipleComponent") ||
               attr.StartsWith("[Serializable") ||
               attr.StartsWith("[System.Serializable") ||
               attr.StartsWith("[Tooltip") ||
               attr.StartsWith("[CreateAssetMenu") ||
               attr.StartsWith("[DefaultExecutionOrder") ||
               attr.StartsWith("[HelpURL") ||
               attr.StartsWith("[ContextMenu") ||
               attr.StartsWith("[ContextMenuItem") ||
               attr.StartsWith("[FormerlySerializedAs") ||
               attr.StartsWith("[RuntimeInitializeOnLoadMethod") ||
               attr.StartsWith("[InitializeOnLoad") ||
               attr.StartsWith("[CustomEditor") ||
               attr.StartsWith("[CustomPropertyDrawer") ||
               attr.StartsWith("[MenuItem") ||
               attr.StartsWith("[DrawGizmo") ||
               attr.StartsWith("[CanEditMultipleObjects") ||
               attr.StartsWith("[PreferBinarySerialization") ||
               attr.StartsWith("[ExcludeFromPresets") ||
               attr.StartsWith("[ExcludeFromObjectFactory") ||
               attr.StartsWith("[SharedBetweenAnimators");
    }

    /// <summary>
    /// Formats a list of attributes and handles proper spacing/inlining.
    /// Returns the formatted attribute string (with newlines) and any remaining inline attributes
    /// that should be prepended to the member declaration.
    /// </summary>
    public static void FormatAttributes(List<string> attributes, string indent, out string standaloneLines, out string inlinePrefix, out bool wantsPrecedingEmptyLine)
    {
        var standaloneSb = new StringBuilder();
        var inlineList = new List<string>();
        wantsPrecedingEmptyLine = false;

        if (attributes == null || attributes.Count == 0)
        {
            standaloneLines = "";
            inlinePrefix = "";
            return;
        }

        foreach (var attr in attributes)
        {
            if (IsBlockStarter(attr))
            {
                wantsPrecedingEmptyLine = true;
                standaloneSb.AppendLine($"{indent}{attr}");
            }
            else if (IsStandalone(attr))
            {
                wantsPrecedingEmptyLine = true; 
                standaloneSb.AppendLine($"{indent}{attr}");
            }
            else
            {
                // Default to inline for everything else (Tooltip, SerializeField, Range, TextArea, etc.)
                inlineList.Add(attr);
            }
        }

        standaloneLines = standaloneSb.ToString();

        if (inlineList.Count > 0)
        {
            var combinedSb = new StringBuilder();
            combinedSb.Append("[");
            for (int i = 0; i < inlineList.Count; i++)
            {
                string cleanAttr = inlineList[i].Trim();
                if (cleanAttr.StartsWith("[")) cleanAttr = cleanAttr.Substring(1);
                if (cleanAttr.EndsWith("]")) cleanAttr = cleanAttr.Substring(0, cleanAttr.Length - 1);
                
                combinedSb.Append(cleanAttr);
                if (i < inlineList.Count - 1)
                {
                    combinedSb.Append(", ");
                }
            }
            combinedSb.Append("] ");
            inlinePrefix = combinedSb.ToString();
        }
        else
        {
            inlinePrefix = "";
        }
    }
}

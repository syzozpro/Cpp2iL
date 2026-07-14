using System;
using System.Linq;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Core;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

public class RegistrationBridge
{
    public void Run(Il2CppContext context)
    {
        if (context.Metadata == null || context.Binary == null)
            throw new InvalidOperationException("Metadata or Elf not loaded");

        context.Registration = new RegistrationResolver(context.BinaryBytes, context.Binary);

        // Build per-type field count array so RegistrationResolver reads exact field
        // offsets from the binary instead of using an arbitrary 256-field limit.
        var fieldCounts = new int[context.Metadata.TypeDefinitions.Length];
        for (int i = 0; i < fieldCounts.Length; i++)
            fieldCounts[i] = context.Metadata.TypeDefinitions[i].FieldCount;

        bool resolved = context.Registration.Resolve(
            expectedTypeDefCount: context.Metadata.TypeDefinitions.Length,
            fieldCounts: fieldCounts);

        if (resolved)
        {
            // MetadataRegistration
            if (context.Registration.MetadataRegistrationVA != 0)
            {
                // V24: extend metadataUsages array to cover string literal slots
                int maxUsageIdx = context.Metadata.MaxMetadataUsageIndex;
                if (maxUsageIdx >= 0 && maxUsageIdx >= context.Registration.MetadataUsageAddresses.Length)
                {
                    context.Registration.ParseMetadataUsagesTable(maxUsageIdx);
                }
            }

            ConsoleReporter.Debug($"  CodeReg ✓ MetaReg ✓");
        }
        else
        {
            ConsoleReporter.Warning("  CodeRegistration not auto-detected. Using metadata-only mode.");
        }

        // Build the bridge
        context.Bridge = new MetadataBinaryBridge(context.Metadata, context.Binary, context.Registration);
        context.Bridge.BuildMap();

        if (resolved)
        {
            ConsoleReporter.Log("IL2Cpp",
                $"Bridge: {context.Bridge.MethodAddressMap.Count:N0} entries ✓");
        }

        if (context.Registration.Types.Length > 0)
        {
            context.TypeResolver = new TypeResolver(context.Metadata, context.Registration);
            context.FieldRvaResolver = new FieldRvaResolver(context.Metadata, context.TypeResolver);

            // Resolve field IsStatic from Il2CppType.Attrs
            var types = context.Registration.Types;
            int staticFieldCount = 0;
            foreach (var fd in context.Metadata.FieldDefinitions)
            {
                if (fd.TypeIndex >= 0 && fd.TypeIndex < types.Length)
                {
                    uint attrs = types[fd.TypeIndex].Attrs;
                    fd.IsStatic = (attrs & 0x0010) != 0;
                    fd.IsLiteral = (attrs & 0x0040) != 0;
                    fd.IsReadOnly = (attrs & 0x0020) != 0;
                    fd.IsNotSerialized = (attrs & 0x0080) != 0;
                    fd.FieldAccess = attrs & 0x07;
                    if (fd.IsStatic) staticFieldCount++;
                }
            }
        }
    }
}

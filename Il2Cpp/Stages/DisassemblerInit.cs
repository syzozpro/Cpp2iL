using System;
using System.Collections.Generic;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.ClangRules;
using Rosetta.Lifter.Disassembly;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

public class DisassemblerInit
{
    public void Run(Il2CppContext context)
    {
        if (context.Metadata == null || context.Binary == null || context.Bridge == null || context.TypeResolver == null || context.Registration == null)
            throw new InvalidOperationException("Required pipeline state missing for DisassemblerInitStage");

        string archName = context.Binary.Is32Bit ? "ARM32 Thumb2" : "ARM64";
        ConsoleReporter.Phase("DISASM", $"Initializing {archName} disassembly engine");
        
        context.CallResolver = new CallResolver(context.Metadata, context.Bridge.MethodAddressMap);
        context.CallResolver.RegisterTypeResolver(context.TypeResolver);
        if (context.TypeModel != null)
            context.CallResolver.RegisterTypeModel(context.TypeModel);

        // Build generic method instantiation resolver
        var genericResolver = new GenericInstanceResolver();
        genericResolver.Build(context.Metadata, context.Registration, context.TypeResolver, isArm32: context.Binary.Is32Bit);
        context.CallResolver.RegisterGenericResolver(genericResolver);
        ConsoleReporter.Success($"  Generic Instantiation VAs: {genericResolver.ResolvedCount:N0}");

        int exhaustiveNew = context.CallResolver.BuildExhaustiveReverseMap(
            context.Registration.ModuleMethodPointers,
            context.Metadata.ImageDefinitions,
            isArm32: context.Binary.Is32Bit);
        if (exhaustiveNew > 0)
            ConsoleReporter.Success($"  Exhaustive VA Scan:       {exhaustiveNew:N0} additional methods resolved");
        if (context.CallResolver.CollisionCount > 0)
            ConsoleReporter.Success($"  VA Collisions Resolved:   {context.CallResolver.CollisionCount:N0} multi-method VAs scored");

        var scanner = new RuntimeHelperScanner(context.BinaryBytes, context.Binary, context.CallResolver);
        scanner.AutoRegisterRuntimeHelpers();
        scanner.PreScanAllMethods(context.Bridge.MethodAddressMap, context.Bridge.ThumbMethodAddresses);

        scanner.ProbeUnresolvedUserMethods(
            context.Registration.ModuleMethodPointers,
            context.Metadata);
        scanner.LabelRemainingUnknowns();

        context.AddressMap = new GlobalAddressMap();
        var bssSection = context.Binary.BssSection;
        if (bssSection.HasValue)
            context.AddressMap.RegisterBssRegion(bssSection.Value.VirtualAddr, bssSection.Value.Size);

        var methodVAToName = new Dictionary<ulong, string>();
        foreach (var (idx, addr) in context.Bridge.MethodAddressMap)
        {
            if (addr == 0 || idx < 0 || idx >= context.Metadata.MethodDefinitions.Length) continue;
            var md = context.Metadata.MethodDefinitions[idx];
            methodVAToName[addr] = md.Name ?? $"method_{idx}";
        }
        context.AddressMap.RegisterMethodPointers(methodVAToName);

        context.AddressMap.RegisterMetadataUsages(context.Registration, context.Metadata, context.TypeResolver);

        var gotResolver = new GotIndirectionResolver();
        gotResolver.Build(context.Binary, context.BinaryBytes);
        var sectionClassifier = new ElfSectionClassifier(context.Binary);
        context.AddressMap.RegisterSectionResolvers(gotResolver, sectionClassifier);
        ConsoleReporter.Success($"  GOT Entries Resolved:     {gotResolver.ResolvedCount:N0}");
        ConsoleReporter.Success($"  Address Map: {context.AddressMap.Count:N0} resolved pointers");

        context.Disassembler = new MethodDisassembler(
            context.BinaryBytes, context.Binary, context.Metadata, context.CallResolver, context.Bridge.MethodAddressMap, context.AddressMap);

        Console.WriteLine();
    }
}
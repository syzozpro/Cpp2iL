using System;
using System.IO;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

public class MetadataLoader
{
    public void Run(Il2CppContext context)
    {
        if (!File.Exists(context.MetadataPath))
        {
            throw new FileNotFoundException($"Metadata file not found: {context.MetadataPath}");
        }

        context.MetadataBytes = File.ReadAllBytes(context.MetadataPath);

        context.Metadata = new MetadataParser(context.MetadataBytes);
        context.Metadata.Parse();

        int metadataLength = context.MetadataBytes.Length;
        context.Metadata.ClearTempMemory();
        context.MetadataBytes = Array.Empty<byte>();

        double mb = metadataLength / 1024.0 / 1024.0;
        ConsoleReporter.Log("IL2Cpp",
            $"Metadata: {mb:F1} MB — " +
            $"{context.Metadata.TypeDefinitions.Length:N0} types, " +
            $"{context.Metadata.MethodDefinitions.Length:N0} methods, " +
            $"{context.Metadata.FieldDefinitions.Length:N0} fields");
    }
}

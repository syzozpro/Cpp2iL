using System;
using System.IO;
using Rosetta.Binary;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

public class BinaryLoader
{
    public void Run(Il2CppContext context)
    {
        if (!File.Exists(context.BinaryPath))
        {
            throw new FileNotFoundException($"Binary file not found: {context.BinaryPath}");
        }

        context.BinaryBytes = File.ReadAllBytes(context.BinaryPath);

        if (context.BinaryBytes.Length >= 2 && context.BinaryBytes[0] == 'M' && context.BinaryBytes[1] == 'Z')
        {
            context.Binary = new PeParser(context.BinaryBytes);
        }
        else
        {
            context.Binary = new ElfParser(context.BinaryBytes);
        }
        context.Binary.Parse();

        string machine = context.Binary.ArchitectureName;
        double mb = context.BinaryBytes.Length / 1024.0 / 1024.0;
        ConsoleReporter.Log("IL2Cpp",
            $"Binary: {mb:F1} MB — {machine} ✓");
    }
}

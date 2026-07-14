using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Build the TypeModel (unified type data layer).
/// Must run after RegistrationBridge (needs Metadata + Registration + TypeResolver).
/// Must run before DisassemblerInit (analysis consumes the model).
/// </summary>
public sealed class TypeModelBuilder
{
    public void Run(Il2CppContext context)
    {
        if (context.Metadata == null || context.Registration == null || context.TypeResolver == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  TypeModelBuilder: Skipped (MetadataRegistration not available — obfuscated or unsupported binary)");
            Console.ResetColor();
            return;
        }

        var model = new TypeModel(context.Metadata, context.Registration, context.TypeResolver);
        model.Build();
        context.TypeModel = model;

        ConsoleReporter.Debug($"  TypeModel: {model.FieldLayoutsBuilt} field layouts, {model.SignaturesBuilt} signatures, {model.TypeNamesResolved} type names");
    }
}
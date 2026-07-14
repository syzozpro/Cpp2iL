namespace Rosetta.Lifter.IR.RuntimeHelpers;

public interface IRuntimeHelperClassifier
{
    string? Classify(RuntimeHelperContext context);
}

namespace Rosetta.Pipeline;

public interface IPipelineStage
{
    string Name { get; }
    void Execute(PipelineContext context);
}

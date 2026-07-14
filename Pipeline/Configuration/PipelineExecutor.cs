using System;
using System.Collections.Generic;

namespace Rosetta.Pipeline;

public class PipelineExecutor
{
    private readonly List<IPipelineStage> _stages = new();

    public PipelineExecutor AddStage(IPipelineStage stage)
    {
        _stages.Add(stage);
        return this;
    }

    public void ExecuteAll(PipelineContext context)
    {
        foreach (var stage in _stages)
        {
            try
            {
                stage.Execute(context);
            }
            catch (Exception ex)
            {
                ConsoleReporter.Error($"Stage '{stage.Name}' failed: {ex.Message}");
                ConsoleReporter.Error(ex.StackTrace ?? "");
                break; // Stop pipeline on failure
            }
        }
    }
}

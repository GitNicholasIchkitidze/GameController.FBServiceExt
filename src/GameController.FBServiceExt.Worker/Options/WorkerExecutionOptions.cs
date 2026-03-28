namespace GameController.FBServiceExt.Worker.Options;

public sealed class WorkerExecutionOptions
{
    public const string SectionName = "WorkerExecution";

    public int RawIngressParallelism { get; set; } = 4;

    public int NormalizedProcessingParallelism { get; set; } = 8;
}

namespace AgenticRAG.Models;

/// <summary>
/// Central shared state passed through the entire workflow via IWorkflowContext.
/// Every executor reads from and writes to this object to track research progress.
/// </summary>
public class RagState
{
    /// <summary>The original user query that started the pipeline.</summary>
    public string UserQuery { get; set; } = "";

    /// <summary>The multi-step research plan created by the Planner.</summary>
    public List<ResearchStep> Plan { get; set; } = new();

    /// <summary>Index of the step currently being processed (0-based).</summary>
    public int CurrentStepIndex { get; set; } = 0;

    /// <summary>
    /// Cumulative one-sentence summaries added by the Reflection agent after each step.
    /// Used by the Policy agent to decide CONTINUE vs FINISH.
    /// </summary>
    public List<string> ResearchHistory { get; set; } = new();

    /// <summary>
    /// Full distilled contexts (one per completed step) used by the Synthesis agent
    /// to generate the final comprehensive answer.
    /// </summary>
    public List<string> DistilledContexts { get; set; } = new();

    /// <summary>Total number of research iterations executed (safety counter).</summary>
    public int IterationCount { get; set; } = 0;

    /// <summary>Hard cap on iterations to prevent infinite loops.</summary>
    public const int MaxIterations = 10;
}

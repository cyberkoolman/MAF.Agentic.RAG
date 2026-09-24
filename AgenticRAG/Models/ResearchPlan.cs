using System.Text.Json.Serialization;

namespace AgenticRAG.Models;

/// <summary>
/// A structured multi-step research plan created by the Planner agent.
/// </summary>
public class ResearchPlan
{
    [JsonPropertyName("steps")]
    public List<ResearchStep> Steps { get; set; } = new();
}

/// <summary>
/// A single step in the research plan, targeting one sub-question with a specific tool.
/// </summary>
public class ResearchStep
{
    /// <summary>The specific sub-question this step answers.</summary>
    [JsonPropertyName("subQuestion")]
    public string SubQuestion { get; set; } = "";

    /// <summary>Why this step is needed in the context of the overall query.</summary>
    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";

    /// <summary>Tool to use: "search_10k" for internal documents or "search_web" for live web.</summary>
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "search_10k";
}

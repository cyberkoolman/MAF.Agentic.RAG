namespace AgenticRAG.Models;

/// <summary>
/// Represents a single document chunk in the knowledge base or web search result.
/// </summary>
public record RagDocument
{
    /// <summary>Unique identifier for this chunk.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Text content of the chunk.</summary>
    public string Content { get; set; } = "";

    /// <summary>Human-readable source name (e.g., "NVIDIA 2024 10-K").</summary>
    public string Source { get; set; } = "";

    /// <summary>Section or category within the source (e.g., "Item 1A. Risk Factors").</summary>
    public string Section { get; set; } = "";

    /// <summary>Vector embedding for semantic search. Empty for web results (not pre-embedded).</summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>Relevance score assigned during retrieval or reranking.</summary>
    public float RelevanceScore { get; set; } = 0f;

    /// <summary>URL pointing back to the original source.</summary>
    public string Url { get; set; } = "";
}

namespace AgenticRAG.Configuration;

public class AppSettings
{
    public AzureAISettings AzureAI { get; set; } = new();
    public TavilySettings Tavily { get; set; } = new();
    public PipelineSettings Pipeline { get; set; } = new();
    public KnowledgeSettings Knowledge { get; set; } = new();
}

public class AzureAISettings
{
    /// <summary>Azure AI Foundry or Azure OpenAI endpoint URL.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>API key for the endpoint. Leave empty to use DefaultAzureCredential.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Deployment name for the high-capability model (planning, policy, synthesis).</summary>
    public string ReasoningModel { get; set; } = "gpt-4o";

    /// <summary>Deployment name for the fast model (rewriting, reranking, distillation, reflection).</summary>
    public string FastModel { get; set; } = "gpt-4o-mini";

    /// <summary>Deployment name for the text embedding model.</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}

public class TavilySettings
{
    /// <summary>Tavily Search API key.</summary>
    public string ApiKey { get; set; } = "";
}

public class PipelineSettings
{
    /// <summary>Approximate token size for each document chunk.</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>Token overlap between consecutive chunks.</summary>
    public int ChunkOverlap { get; set; } = 50;

    /// <summary>Number of documents to retrieve before reranking.</summary>
    public int InitialRetrievalTopK { get; set; } = 10;

    /// <summary>Number of documents to keep after reranking.</summary>
    public int RerankerTopK { get; set; } = 3;

    /// <summary>Safety cap on research iterations to prevent infinite loops.</summary>
    public int MaxIterations { get; set; } = 10;
}

public class KnowledgeSettings
{
    /// <summary>
    /// One or more document sources to index into the knowledge base.
    /// Each source can be a remote URL or a local file path.
    /// </summary>
    public List<DocumentSource> Sources { get; set; } = new();

    /// <summary>
    /// Optional pre-set query shown as a prompt default.
    /// Leave empty to require the user to type a query at runtime.
    /// </summary>
    public string DefaultQuery { get; set; } = "";
}

public class DocumentSource
{
    /// <summary>Human-readable name used in citations (e.g. "NVIDIA 2024 10-K").</summary>
    public string Name { get; set; } = "";

    /// <summary>Remote URL to download. Leave empty if using FilePath.</summary>
    public string Url { get; set; } = "";

    /// <summary>Local file path. Leave empty if using Url.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Content type: "html" (default) or "text".</summary>
    public string Type { get; set; } = "html";

    /// <summary>Optional short description passed to the Planner so it knows what the source contains.</summary>
    public string Description { get; set; } = "";
}

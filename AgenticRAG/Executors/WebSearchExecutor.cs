using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 3b — Web Search (Tavily)
///
/// Handles SearchRequests routed here when tool == "search_web".
/// Augments the static knowledge base with live web results, bridging the gap
/// between the source date and current events.
///
/// Results are returned as RagDocument objects so they flow through the same
/// Reranker → Distiller → Reflection funnel as internal documents.
/// </summary>
[SendsMessage(typeof(SearchResults))]
public sealed class WebSearchExecutor : Executor<SearchRequest>
{
    private readonly TavilyService _tavily;

    public WebSearchExecutor(TavilyService tavily) : base("WebSearch") => _tavily = tavily;

    public override async ValueTask HandleAsync(
        SearchRequest message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n[WebSearch] Querying Tavily...");
        Console.WriteLine($"  Query : {message.RewrittenQuery}");

        var results = await _tavily.SearchAsync(
            message.RewrittenQuery,
            maxResults: 5,
            cancellationToken);

        Console.WriteLine($"  Retrieved {results.Count} web results.");

        await context.SendMessageAsync(
            new SearchResults(results, message.OriginalSubQuestion, message.StepIndex),
            cancellationToken: cancellationToken);
    }
}

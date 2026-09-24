using AgenticRAG.Configuration;
using AgenticRAG.Executors;
using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Workflow;

/// <summary>
/// Assembles the One-Shot RAG workflow — a simple linear pipeline for comparison
/// with the deep-thinking agentic workflow.
///
/// Graph topology (no loops):
///
///   UserInput (string)
///       │
///   [Gateway]          — URL loading / no-source check / forward query
///       │ UserQuery
///   [QueryBridge]      — maps raw query → SearchRequest (no LLM, no rewriting)
///       │ SearchRequest
///   [VectorSearch]     — strategy supervisor + broad recall (top-K)
///       │ SearchResults
///   [Reranker]         — cross-encoder precision filter (top-3)
///       │ RankedResults
///   [OneShotAnswer]    — single LLM call → yield output
///
/// What is intentionally missing (to contrast with the agentic pipeline):
///   • No Planner         — query is not decomposed into sub-questions
///   • No QueryRewriter   — raw query goes directly to search
///   • No Web Search      — no tool selection; docs only
///   • No Distiller       — raw chunks go to the LLM
///   • No Reflection      — no cumulative research memory
///   • No Policy Agent    — no continue/re-think/finish loop
///   • No Synthesis       — no multi-step evidence integration
/// </summary>
public static class OneShotRagWorkflow
{
    public static Microsoft.Agents.AI.Workflows.Workflow Build(
        AzureAIService     aiService,
        VectorStore        vectorStore,
        TavilyService      tavilyService,
        DocumentLoader     documentLoader,
        KnowledgeBaseState kbState,
        PipelineSettings   pipelineSettings,
        string             name = "")
    {
        // ── Reused executor nodes ─────────────────────────────────────────
        var gateway      = new GatewayExecutor(aiService, vectorStore, documentLoader, kbState);
        var queryBridge  = new QueryBridgeExecutor();
        var vectorSearch = new VectorSearchExecutor(aiService, vectorStore, pipelineSettings.InitialRetrievalTopK);
        var reranker     = new RerankerExecutor(aiService, pipelineSettings.RerankerTopK);
        var answer       = new OneShotAnswerExecutor(aiService);

        // ── Wire linear graph ─────────────────────────────────────────────
        var workflowBuilder = new WorkflowBuilder(gateway)

            // Gateway → QueryBridge (forward all UserQuery messages)
            .AddEdge<UserQuery>(gateway, queryBridge, condition: _ => true)

            // QueryBridge → VectorSearch (always search_docs)
            .AddEdge(queryBridge, vectorSearch)

            // VectorSearch → Reranker
            .AddEdge(vectorSearch, reranker)

            // Reranker → OneShotAnswer (terminal)
            .AddEdge(reranker, answer)

            .WithOutputFrom(answer);

        if (!string.IsNullOrEmpty(name))
            workflowBuilder = workflowBuilder.WithName(name);

        return workflowBuilder.Build();
    }

    // ── Bridge executor ───────────────────────────────────────────────────
    // Converts a UserQuery into a SearchRequest with the raw query text.
    // No LLM call, no rewriting — intentionally naive.

    [SendsMessage(typeof(SearchRequest))]
    private sealed class QueryBridgeExecutor : Executor<UserQuery>
    {
        public QueryBridgeExecutor() : base("QueryBridge") { }

        public override ValueTask HandleAsync(
            UserQuery message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"\n[QueryBridge] Passing raw query to vector search (no rewriting)");
            Console.WriteLine($"  Query: {message.Query}");

            return context.SendMessageAsync(
                new SearchRequest(
                    RewrittenQuery:     message.Query,
                    Tool:               "search_docs",
                    OriginalSubQuestion: message.Query,
                    StepIndex:          0),
                cancellationToken: cancellationToken);
        }
    }
}

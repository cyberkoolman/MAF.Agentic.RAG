using AgenticRAG.Configuration;
using AgenticRAG.Executors;
using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Workflow;

/// <summary>
/// Assembles all executor nodes into the complete Deep-Thinking RAG workflow graph.
///
/// Graph topology (edges):
///
///   UserQuery
///       │
///   [Planner]  ──────────────────────────────────────────────────────┐
///       │ StepSignal(0)                                              │
///       ▼                                                            │
///   [QueryRewriter] ◄──── StepSignal(n+1) ─── [Policy] ◄─────────────┤
///       │ SearchRequest                           │ FinishSignal     │
///       ├─── tool=="search_docs" ──► [VectorSearch]│                 │
///       └─── tool=="search_web" ──► [WebSearch]  ▼                   │
///                                    │       [Synthesis]             │
///                                    │ SearchResults  │ yield output │
///                                    ▼               ▼               │
///                              [Reranker]        workflow END        │
///                                    │ RankedResults                 │
///                                    ▼                               │
///                              [Distiller]                           │
///                                    │ DistilledContext              │
///                                    ▼                               │
///                              [Reflection] ─── PolicySignal ────────┘
///
/// Key design decisions:
///   • StepSignal is sent by BOTH Planner and Policy → enables the feedback loop
///     without requiring a separate gateway executor.
///   • Conditional edges from QueryRewriter route on SearchRequest.Tool.
///   • Both VectorSearch and WebSearch output SearchResults → Reranker handles both.
///   • PolicyExecutor sends StepSignal (CONTINUE) or FinishSignal (FINISH);
///     conditional edges split these to QueryRewriter and Synthesis respectively.
///   • SynthesisExecutor calls YieldOutputAsync → terminates the loop.
/// </summary>
public static class AgenticRagWorkflow
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
        // ── Instantiate executor nodes ────────────────────────────────────────
        var gateway       = new GatewayExecutor(aiService, vectorStore, documentLoader, kbState);
        var planner       = new PlannerExecutor(aiService, kbState);
        var queryRewriter = new QueryRewriterExecutor(aiService);
        var vectorSearch  = new VectorSearchExecutor(aiService, vectorStore, pipelineSettings.InitialRetrievalTopK);
        var webSearch     = new WebSearchExecutor(tavilyService);
        var reranker      = new RerankerExecutor(aiService, pipelineSettings.RerankerTopK);
        var distiller     = new DistillerExecutor(aiService);
        var reflection    = new ReflectionExecutor(aiService);
        var policy        = new PolicyExecutor(aiService);
        var synthesis     = new SynthesisExecutor(aiService);

        // ── Wire graph edges ──────────────────────────────────────────────────
        // AddEdge<T> type parameter acts as an implicit message-type filter:
        // only messages of type T are evaluated against the optional condition.
        var workflowBuilder = new WorkflowBuilder(gateway)

            // 0. Gateway routes queries to Planner (source-loading and no-source
            //    cases are handled internally via YieldOutputAsync)
            .AddEdge<UserQuery>(gateway, planner, condition: _ => true)

            // 1. Planning → first research step (unconditional, single message type)
            .AddEdge(planner, queryRewriter)

            // 2. Route to the correct search tool based on SearchRequest.Tool
            //    The <SearchRequest> type parameter restricts these edges to SearchRequest messages only.
            .AddEdge<SearchRequest>(queryRewriter, vectorSearch,
                condition: msg => msg?.Tool == "search_docs")
            .AddEdge<SearchRequest>(queryRewriter, webSearch,
                condition: msg => msg?.Tool == "search_web")

            // 3. Both search paths converge at Reranker (unconditional)
            .AddEdge(vectorSearch, reranker)
            .AddEdge(webSearch,    reranker)

            // 4. Retrieval funnel: Reranker → Distiller → Reflection (unconditional)
            .AddEdge(reranker,    distiller)
            .AddEdge(distiller,   reflection)
            .AddEdge(reflection,  policy)

            // 5. Policy CONTINUE: StepSignal loops back to QueryRewriter.
            //    <StepSignal> type-filters so only StepSignal triggers this edge.
            //    Always-true condition provided to resolve overload ambiguity.
            .AddEdge<StepSignal>(policy, queryRewriter, condition: _ => true)

            // 6. Policy FINISH: FinishSignal proceeds to Synthesis.
            //    <FinishSignal> type-filters so only FinishSignal triggers this edge.
            .AddEdge<FinishSignal>(policy, synthesis, condition: _ => true)

            // 7. Synthesis yields the workflow output and terminates the loop
            .WithOutputFrom(synthesis);

        if (!string.IsNullOrEmpty(name))
            workflowBuilder = workflowBuilder.WithName(name);

        return workflowBuilder.Build();
    }
}

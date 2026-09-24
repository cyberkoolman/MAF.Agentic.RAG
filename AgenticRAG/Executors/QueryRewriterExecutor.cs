using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 2 — Query Rewriter
///
/// Receives a StepSignal from either the Planner (first iteration) or the Policy
/// agent (subsequent iterations after a CONTINUE decision). Reads the current
/// research step from shared state and decides how to form the search query:
///
///   • search_docs, no prior history → passes the original user query through
///     unchanged — identical to QueryBridge in the one-shot pipeline. The Planner's
///     sub-question is a narrower paraphrase that retrieves different chunks; using
///     the same starting query ensures both pipelines begin from the same retrieval.
///   • search_docs, with prior history → rewrites incorporating previous findings
///     so the query targets what is still unknown.
///   • search_web (any step) → rewrites for current web search terminology.
///
/// Outgoing message: SearchRequest → routed by conditional edges to
///   VectorSearchExecutor (tool == "search_docs") or
///   WebSearchExecutor    (tool == "search_web")
/// </summary>
[SendsMessage(typeof(SearchRequest))]
public sealed class QueryRewriterExecutor : Executor<StepSignal>
{
    private readonly AzureAIService _ai;

    public QueryRewriterExecutor(AzureAIService ai) : base("QueryRewriter") => _ai = ai;

    public override async ValueTask HandleAsync(
        StepSignal message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<RagState>(
                        PlannerExecutor.StateKey,
                        scopeName: PlannerExecutor.StateScope,
                        cancellationToken)
                    ?? throw new InvalidOperationException("RAG state not found in workflow context.");

        // Sync the current step index in case Policy advanced it
        state.CurrentStepIndex = message.StepIndex;
        state.IterationCount++;

        // Guard: should not happen because Policy checks bounds, but be defensive
        if (message.StepIndex >= state.Plan.Count)
        {
            Console.WriteLine("[QueryRewriter] Step index out of range — skipping to finish.");
            return;
        }

        var step = state.Plan[message.StepIndex];
        Console.WriteLine("\n" + new string('─', 80));
        Console.WriteLine($"[QueryRewriter] Step {message.StepIndex + 1}/{state.Plan.Count}");
        Console.WriteLine($"  Sub-question : {step.SubQuestion}");
        Console.WriteLine($"  Tool         : {step.Tool}");

        // Build context from previous findings to guide query optimisation
        var historyContext = state.ResearchHistory.Count > 0
            ? $"\n\nPrevious findings:\n{string.Join("\n", state.ResearchHistory)}"
            : string.Empty;

        string rewritten;

        // For search_docs with no prior research context, use the original user query
        // directly — identical to what QueryBridge sends in the one-shot pipeline.
        // The Planner's sub-question is a narrower paraphrase and would retrieve different
        // (often worse) chunks. Agentic value comes from steps 2+ where prior findings
        // refocus the query on what is still unknown.
        if (step.Tool == "search_docs" && state.ResearchHistory.Count == 0)
        {
            rewritten = state.UserQuery;
            Console.WriteLine($"  Rewritten    : {rewritten}  [passthrough — original query]");
        }
        else
        {
            const string SystemPrompt = """
                You are a search query optimisation specialist.

                Rewrite the given sub-question into the single most effective search query for
                the specified tool:
                  • search_docs → incorporate key findings from previous research steps to focus
                                  the query on what is still unknown. Keep natural language phrasing
                                  for conceptual questions; use precise keyword form only for exact
                                  section titles, product model numbers, or financial figures.
                  • search_web  → use specific, current terms that will surface recent news,
                                  analyst reports, or press releases.

                Return ONLY the rewritten query string — no explanation, no punctuation wrappers.
                """;

            rewritten = (await _ai.CompleteAsync(
                SystemPrompt,
                $"Sub-question : {step.SubQuestion}\nTool         : {step.Tool}{historyContext}\n\nRewritten query:",
                useReasoningModel: false,
                cancellationToken)).Text;

            rewritten = rewritten.Trim().Trim('"').Trim('\'');
            Console.WriteLine($"  Rewritten    : {rewritten}");
        }

        // Persist the updated iteration count
        await context.QueueStateUpdateAsync(
            PlannerExecutor.StateKey, state,
            scopeName: PlannerExecutor.StateScope,
            cancellationToken);

        await context.SendMessageAsync(
            new SearchRequest(rewritten, step.Tool, step.SubQuestion, message.StepIndex),
            cancellationToken: cancellationToken);
    }
}

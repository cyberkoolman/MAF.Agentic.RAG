namespace AgenticRAG.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Workflow message types — each record is the typed payload flowing over an edge
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Initial trigger sent to PlannerExecutor by Program.cs.
/// </summary>
public record UserQuery(string Query);

/// <summary>
/// Sent by PlannerExecutor (step 0) and PolicyExecutor (subsequent steps) to
/// QueryRewriterExecutor. Carries the index of the step to process next.
/// This shared type is what enables the feedback loop.
/// </summary>
public record StepSignal(int StepIndex);

/// <summary>
/// Sent by QueryRewriterExecutor to the appropriate search executor.
/// The Tool field routes the message via conditional edges.
/// </summary>
public record SearchRequest(
    string RewrittenQuery,
    string Tool,                 // "search_docs" (internal KB) | "search_web" (Tavily)
    string OriginalSubQuestion,
    int StepIndex);

/// <summary>
/// Sent by VectorSearchExecutor and WebSearchExecutor to RerankerExecutor.
/// </summary>
public record SearchResults(
    List<RagDocument> Documents,
    string SubQuestion,
    int StepIndex);

/// <summary>
/// Sent by RerankerExecutor to DistillerExecutor after precision filtering.
/// </summary>
public record RankedResults(
    List<RagDocument> TopDocuments,
    string SubQuestion,
    int StepIndex);

/// <summary>
/// Sent by DistillerExecutor to ReflectionExecutor with synthesized context.
/// </summary>
public record DistilledContext(
    string Context,
    string SubQuestion,
    int StepIndex);

/// <summary>
/// Sent by ReflectionExecutor to PolicyExecutor after updating research history.
/// </summary>
public record PolicySignal(int CompletedStepIndex);

/// <summary>
/// Sent by PolicyExecutor to SynthesisExecutor when research is complete.
/// This is the FINISH branch of the conditional split.
/// </summary>
public record FinishSignal();

/// <summary>
/// Final workflow output carrying the answer text and cumulative token usage.
/// Yielded by SynthesisExecutor and OneShotAnswerExecutor.
/// </summary>
public record AnswerResult(string Text, int InputTokens, int OutputTokens, int TotalTokens);

using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 8 — Synthesis Agent (terminal node)
///
/// Triggered by the Policy agent's FINISH decision. Reads the complete research
/// history and all distilled contexts from shared state, then uses the reasoning
/// LLM to synthesise a single comprehensive, citable answer.
///
/// This is the only node that calls context.YieldOutputAsync — it terminates the
/// workflow and delivers the final result to the caller in Program.cs.
/// </summary>
[YieldsOutput(typeof(AnswerResult))]
public sealed class SynthesisExecutor : Executor<FinishSignal>
{
    private readonly AzureAIService _ai;

    public SynthesisExecutor(AzureAIService ai) : base("Synthesis") => _ai = ai;

    public override async ValueTask HandleAsync(
        FinishSignal message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<RagState>(
                        PlannerExecutor.StateKey,
                        scopeName: PlannerExecutor.StateScope,
                        cancellationToken)
                    ?? throw new InvalidOperationException("RAG state not found.");

        Console.WriteLine("\n" + new string('─', 80));
        Console.WriteLine("[Synthesis] Generating final comprehensive answer...");
        Console.WriteLine($"  Research steps completed : {state.ResearchHistory.Count}");

        // Build the evidence block from distilled contexts (detailed) +
        // research history (summaries) for the LLM
        var evidenceBlock = state.DistilledContexts.Count > 0
            ? string.Join("\n\n", state.DistilledContexts)
            : string.Join("\n", state.ResearchHistory);

        var historySummary = string.Join("\n", state.ResearchHistory);

        const string SystemPrompt = """
            You are a senior research synthesis agent.

            Your task: write a comprehensive, well-structured answer to the original query
            by integrating all research findings.

            Guidelines:
            1. Perform multi-hop reasoning — explicitly connect facts across sources.
            2. Use specific details: cite sources inline as [Source Name / Section](URL) when a URL is present in the evidence, preserving markdown links exactly as provided.
            3. Structure the answer with clear headings if complexity warrants it.
            4. Be precise: include numbers, dates, product names where available.
            5. Address every aspect of the original query.
            6. End with a concise synthesis paragraph that connects all findings.
            7. Acknowledge where information is limited or uncertain.
            """;

        var result = await _ai.CompleteAsync(
            SystemPrompt,
            $"Original query:\n{state.UserQuery}\n\n" +
            $"Research summary:\n{historySummary}\n\n" +
            $"Detailed evidence:\n{evidenceBlock}\n\n" +
            "Write the comprehensive answer:",
            useReasoningModel: true,
            cancellationToken);

        // Print to console for immediate visibility
        Console.WriteLine("\n" + new string('═', 80));
        Console.WriteLine("FINAL ANSWER");
        Console.WriteLine(new string('═', 80));
        Console.WriteLine(result.Text);
        Console.WriteLine(new string('═', 80));
        Console.WriteLine($"  Tokens — input: {result.InputTokens}, output: {result.OutputTokens}, total: {result.TotalTokens}");

        // Yield to the workflow caller (Program.cs)
        await context.YieldOutputAsync(
            new AnswerResult(result.Text, result.InputTokens, result.OutputTokens, result.TotalTokens),
            cancellationToken);
    }
}

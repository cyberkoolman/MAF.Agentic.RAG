using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// One-Shot RAG — Terminal answer node.
///
/// Receives the reranked top-K documents and generates a final answer in a single
/// LLM call. Unlike the agentic pipeline this executor performs:
///   • No contextual distillation (raw chunks go straight to the LLM)
///   • No reflection or cumulative memory
///   • No policy loop — one pass and done
///
/// This intentionally naive approach highlights the quality gap that planning,
/// reflection, and iterative retrieval provide in the deep-thinking pipeline.
/// </summary>
[YieldsOutput(typeof(AnswerResult))]
public sealed class OneShotAnswerExecutor : Executor<RankedResults>
{
    private readonly AzureAIService _ai;

    public OneShotAnswerExecutor(AzureAIService ai) : base("OneShotAnswer") => _ai = ai;

    public override async ValueTask HandleAsync(
        RankedResults message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n" + new string('─', 80));
        Console.WriteLine("[OneShotAnswer] Generating answer from reranked documents (single pass)...");
        Console.WriteLine($"  Documents available: {message.TopDocuments.Count}");

        if (message.TopDocuments.Count == 0)
        {
            const string fallback = "I could not find relevant information to answer your question.";
            Console.WriteLine($"  → {fallback}");
            await context.YieldOutputAsync(new AnswerResult(fallback, 0, 0, 0), cancellationToken);
            return;
        }

        // Concatenate raw documents — no distillation step
        var docs = string.Join("\n\n---\n\n", message.TopDocuments.Select(d =>
            $"Source : {d.Source}\nSection: {d.Section}\nURL    : {d.Url}\n\n{d.Content}"));

        const string SystemPrompt = """
            You are a helpful research assistant.

            Answer the user's question using ONLY the provided documents.
            - Cite sources inline using [Source Name](URL) when a URL is available.
            - Be precise: include specific numbers, dates, and names when present.
            - If the documents do not contain enough information, say so clearly.
            - Do NOT speculate or add information beyond what the documents provide.
            """;

        var result = await _ai.CompleteAsync(
            SystemPrompt,
            $"Question: {message.SubQuestion}\n\nDocuments:\n{docs}\n\nAnswer:",
            useReasoningModel: true,
            cancellationToken);

        Console.WriteLine("\n" + new string('═', 80));
        Console.WriteLine("FINAL ANSWER (One-Shot)");
        Console.WriteLine(new string('═', 80));
        Console.WriteLine(result.Text);
        Console.WriteLine(new string('═', 80));
        Console.WriteLine($"  Tokens — input: {result.InputTokens}, output: {result.OutputTokens}, total: {result.TotalTokens}");

        await context.YieldOutputAsync(
            new AnswerResult(result.Text, result.InputTokens, result.OutputTokens, result.TotalTokens),
            cancellationToken);
    }
}

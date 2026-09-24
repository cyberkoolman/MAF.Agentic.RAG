using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 5 — Contextual Distiller
///
/// Retrieval funnel — Stage 3: Synthesis
///
/// Receives the top-K reranked documents and compresses them into a single,
/// dense paragraph of evidence relevant to the sub-question.
///
/// Benefits:
///   - Removes redundancy across overlapping chunks
///   - Preserves precise facts (numbers, dates, names) and inline citations
///   - Reduces the token budget for downstream agents
/// </summary>
[SendsMessage(typeof(DistilledContext))]
public sealed class DistillerExecutor : Executor<RankedResults>
{
    private readonly AzureAIService _ai;

    public DistillerExecutor(AzureAIService ai) : base("Distiller") => _ai = ai;

    public override async ValueTask HandleAsync(
        RankedResults message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\n[Distiller] Distilling {message.TopDocuments.Count} documents...");

        if (message.TopDocuments.Count == 0)
        {
            Console.WriteLine("  No documents to distill — returning empty context.");
            await context.SendMessageAsync(
                new DistilledContext("No relevant information found.", message.SubQuestion, message.StepIndex),
                cancellationToken: cancellationToken);
            return;
        }

        var docs = string.Join("\n\n---\n\n", message.TopDocuments.Select(d =>
            $"Source : {d.Source}\nSection: {d.Section}\nURL    : {d.Url}\n\n{d.Content}"));

        const string SystemPrompt = """
            You are a contextual distillation agent.

            Task: Synthesise the provided document excerpts into a single, dense paragraph
            that captures all information needed to answer the question.

            Rules:
            - Preserve exact numbers, dates, product names, and proper nouns.
            - Cite each fact inline using [Source Name](URL) markdown-link format when a URL is available, or [Source Name] when no URL is present.
            - Use the EXACT URL from the "URL:" field — never construct, shorten, or modify it.
            - Remove redundancy; never repeat the same fact twice.
            - Do NOT speculate or add information not present in the documents.
            - Keep the distilled paragraph under 300 words.
            """;

        var distilled = (await _ai.CompleteAsync(
            SystemPrompt,
            $"Question: {message.SubQuestion}\n\nDocuments:\n{docs}\n\nDistilled context:",
            useReasoningModel: false,
            cancellationToken)).Text;

        distilled = distilled.Trim();
        Console.WriteLine($"  Distilled to {distilled.Length} characters.");

        await context.SendMessageAsync(
            new DistilledContext(distilled, message.SubQuestion, message.StepIndex),
            cancellationToken: cancellationToken);
    }
}

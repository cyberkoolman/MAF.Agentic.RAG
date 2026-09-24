using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 6 — Reflection Agent
///
/// After each retrieval step the agent pauses to reflect: it reads the distilled
/// evidence, produces a concise one-sentence summary of what was learned, and
/// appends it to the cumulative research history in shared state.
///
/// The research history is the primary input for the Policy agent's
/// CONTINUE vs FINISH decision, and for the Synthesis agent's final answer.
/// </summary>
[SendsMessage(typeof(PolicySignal))]
public sealed class ReflectionExecutor : Executor<DistilledContext>
{
    private readonly AzureAIService _ai;

    public ReflectionExecutor(AzureAIService ai) : base("Reflection") => _ai = ai;

    public override async ValueTask HandleAsync(
        DistilledContext message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<RagState>(
                        PlannerExecutor.StateKey,
                        scopeName: PlannerExecutor.StateScope,
                        cancellationToken)
                    ?? throw new InvalidOperationException("RAG state not found.");

        Console.WriteLine($"\n[Reflection] Reflecting on Step {message.StepIndex + 1} findings...");

        const string SystemPrompt = """
            You are a research reflection agent.

            Summarise the key finding from the retrieved context in exactly one concise sentence.
            Be specific: include concrete facts, numbers, or named entities where present.
            The sentence will be added to a research log read by a policy agent.
            """;

        var summary = (await _ai.CompleteAsync(
            SystemPrompt,
            $"Original query  : {state.UserQuery}\n" +
            $"Sub-question    : {message.SubQuestion}\n" +
            $"Retrieved context:\n{message.Context}",
            useReasoningModel: false,
            cancellationToken)).Text;

        summary = summary.Trim().TrimEnd('.');

        var stepLabel = $"Step {message.StepIndex + 1} [{state.Plan[message.StepIndex].Tool}]: {summary}.";
        state.ResearchHistory.Add(stepLabel);
        state.DistilledContexts.Add(
            $"=== Step {message.StepIndex + 1}: {message.SubQuestion} ===\n{message.Context}");

        Console.WriteLine($"  → {stepLabel}");

        // Persist updated history so Policy and Synthesis can read it
        await context.QueueStateUpdateAsync(
            PlannerExecutor.StateKey, state,
            scopeName: PlannerExecutor.StateScope,
            cancellationToken);

        await context.SendMessageAsync(
            new PolicySignal(message.StepIndex),
            cancellationToken: cancellationToken);
    }
}

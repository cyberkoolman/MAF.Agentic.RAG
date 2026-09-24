using System.Text.Json.Serialization;
using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 7 — Policy Agent (Control Flow)
///
/// The master strategist of the pipeline. After each research step it examines:
///   - The original query
///   - The overall research plan (completed vs pending steps)
///   - The accumulated research history
///
/// And makes one of two decisions:
///   CONTINUE → sends StepSignal(nextIndex) back to QueryRewriterExecutor (loop)
///   FINISH   → sends FinishSignal to SynthesisExecutor (exits loop)
///
/// Hard-stop rules (checked before the LLM call):
///   - All plan steps are exhausted
///   - Maximum iteration count reached (prevents runaway loops)
/// </summary>
[SendsMessage(typeof(StepSignal))]
[SendsMessage(typeof(FinishSignal))]
public sealed class PolicyExecutor : Executor<PolicySignal>
{
    private readonly AzureAIService _ai;

    public PolicyExecutor(AzureAIService ai) : base("Policy") => _ai = ai;

    public override async ValueTask HandleAsync(
        PolicySignal message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<RagState>(
                        PlannerExecutor.StateKey,
                        scopeName: PlannerExecutor.StateScope,
                        cancellationToken)
                    ?? throw new InvalidOperationException("RAG state not found.");

        int nextStep = message.CompletedStepIndex + 1;
        Console.WriteLine($"\n[Policy] Evaluating after Step {message.CompletedStepIndex + 1}...");

        // ── Hard stops ───────────────────────────────────────────────────────
        if (nextStep >= state.Plan.Count)
        {
            Console.WriteLine("  → FINISH (plan exhausted)");
            await context.SendMessageAsync(new FinishSignal(), cancellationToken: cancellationToken);
            return;
        }

        if (state.IterationCount >= RagState.MaxIterations)
        {
            Console.WriteLine("  → FINISH (max iterations reached)");
            await context.SendMessageAsync(new FinishSignal(), cancellationToken: cancellationToken);
            return;
        }

        // ── LLM policy decision ──────────────────────────────────────────────
        var planSummary = string.Join("\n", state.Plan.Select((s, i) =>
        {
            var status = i <= message.CompletedStepIndex ? "✓ DONE" : "○ PENDING";
            return $"  {i + 1}. [{s.Tool,-12}] {status}  {s.SubQuestion}";
        }));

        var history = state.ResearchHistory.Count > 0
            ? string.Join("\n", state.ResearchHistory)
            : "(none yet)";

        const string SystemPrompt = """
            You are a research policy agent that controls an iterative RAG loop.

            Decide whether to CONTINUE (execute the next pending research step) or FINISH
            (the gathered information is already sufficient to answer the query).

            Choose FINISH only when:
              - The research history meaningfully addresses all key aspects of the query.
              - Running more steps would yield diminishing returns.

            Choose CONTINUE when:
              - There are pending steps that will add genuinely new, necessary information.

            Return JSON exactly:
            { "action": "CONTINUE" }   or   { "action": "FINISH" }
            """;

        var decision = await _ai.CompleteStructuredAsync<PolicyDecision>(
            SystemPrompt,
            $"Original query:\n{state.UserQuery}\n\nPlan:\n{planSummary}\n\nResearch history:\n{history}",
            useReasoningModel: true,
            cancellationToken);

        var action = decision?.Action?.ToUpperInvariant() == "FINISH" ? "FINISH" : "CONTINUE";
        Console.WriteLine($"  → {action}");

        if (action == "FINISH")
            await context.SendMessageAsync(new FinishSignal(), cancellationToken: cancellationToken);
        else
            await context.SendMessageAsync(new StepSignal(nextStep), cancellationToken: cancellationToken);
    }

    // ── Private DTOs ──────────────────────────────────────────────────────────

    private sealed class PolicyDecision
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }
    }
}

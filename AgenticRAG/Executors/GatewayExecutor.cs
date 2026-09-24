using System.Text.RegularExpressions;
using AgenticRAG.Configuration;
using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.Workflows;

namespace AgenticRAG.Executors;

/// <summary>
/// Node 0 — Gateway (root node)
///
/// Intercepts every user message before the planning loop:
///   • URL / file path detected → loads and indexes the document, yields a confirmation.
///   • No source loaded yet     → yields a prompt asking for a URL.
///   • Has source + plain query → forwards the message to the Planner.
/// </summary>
[YieldsOutput(typeof(string))]
[SendsMessage(typeof(UserQuery))]
public sealed class GatewayExecutor : Executor<string>
{
    private readonly AzureAIService     _ai;
    private readonly VectorStore        _store;
    private readonly DocumentLoader     _loader;
    private readonly KnowledgeBaseState _kbState;

    public GatewayExecutor(
        AzureAIService     ai,
        VectorStore        store,
        DocumentLoader     loader,
        KnowledgeBaseState kbState) : base("Gateway")
    {
        _ai      = ai;
        _store   = store;
        _loader  = loader;
        _kbState = kbState;
    }

    public override async ValueTask HandleAsync(
        string            message,
        IWorkflowContext  context,
        CancellationToken ct = default)
    {
        var text = message?.Trim() ?? "";
        var url  = ExtractUrl(text);

        // ── Source loading ─────────────────────────────────────────────────
        if (url is not null)
        {
            var source = new DocumentSource
            {
                Name     = UrlToName(url),
                Url      = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "",
                FilePath = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "" : url,
                Type     = "html",
            };

            Console.WriteLine($"[Gateway] Loading source: {url}");
            var docs = await _loader.LoadAllAsync([source], _ai, ct);
            _store.AddDocuments(docs);
            _kbState.AppendSource(source.Name);

            Console.WriteLine($"[Gateway] Indexed {docs.Count} chunks. Total: {_store.DocumentCount}");

            await context.YieldOutputAsync(
                $"Done! Indexed **{docs.Count} chunks** from `{source.Name}`. " +
                $"Knowledge base now has **{_store.DocumentCount} chunks** total.\n\n" +
                "What would you like to know?",
                ct);
            return;
        }

        // ── No source yet ──────────────────────────────────────────────────
        if (!_kbState.HasSource)
        {
            await context.YieldOutputAsync(
                "I'm ready! Please share a URL (or paste a file path) to load your " +
                "knowledge source, and I'll index it. Then ask me anything.",
                ct);
            return;
        }

        // ── Query — forward to Planner ─────────────────────────────────────
        await context.SendMessageAsync(new UserQuery(text), ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? ExtractUrl(string text)
    {
        var urlMatch = Regex.Match(text, @"https?://[^\s]+", RegexOptions.IgnoreCase);
        if (urlMatch.Success) return urlMatch.Value;

        var pathMatch = Regex.Match(text, @"[A-Za-z]:\\[^\s]+|/[^\s]+", RegexOptions.IgnoreCase);
        if (pathMatch.Success && File.Exists(pathMatch.Value)) return pathMatch.Value;

        return null;
    }

    private static string UrlToName(string url)
    {
        try
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri  = new Uri(url);
                var host = uri.Host.Replace("www.", "");
                var path = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
                return string.IsNullOrEmpty(path) ? host : $"{host}/{path}";
            }
            return Path.GetFileName(url);
        }
        catch { return url; }
    }
}

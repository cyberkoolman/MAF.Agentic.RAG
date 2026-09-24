using AgenticRAG.Configuration;
using AgenticRAG.Models;
using AgenticRAG.Services;
using AgenticRAG.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Agents.AI.Workflows;

// ─────────────────────────────────────────────────────────────────────────────
// Bootstrap
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json",       optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true,  reloadOnChange: false) // git-ignored; overrides keys
    .Build();

var settings = config.Get<AppSettings>()
    ?? throw new InvalidOperationException("Failed to load appsettings.json.");

ValidateSettings(settings);

Console.WriteLine(new string('═', 80));
Console.WriteLine("  Agentic Deep-Thinking RAG Pipeline");
Console.WriteLine("  Microsoft Agent Framework  |  Azure AI Foundry  |  Tavily Search");
Console.WriteLine(new string('═', 80));
Console.WriteLine($"  Reasoning model : {settings.AzureAI.ReasoningModel}");
Console.WriteLine($"  Fast model      : {settings.AzureAI.FastModel}");
Console.WriteLine($"  Embedding model : {settings.AzureAI.EmbeddingModel}");
Console.WriteLine($"  Chunk size      : {settings.Pipeline.ChunkSize} tokens");
Console.WriteLine($"  Retrieval top-K : {settings.Pipeline.InitialRetrievalTopK}  →  Reranker top-K: {settings.Pipeline.RerankerTopK}");
Console.WriteLine(new string('─', 80));

// ─────────────────────────────────────────────────────────────────────────────
// Initialise services
// ─────────────────────────────────────────────────────────────────────────────

var aiService      = new AzureAIService(settings);
var vectorStore    = new VectorStore();
var tavilyService  = new TavilyService(settings);
var documentLoader = new DocumentLoader(settings);

// ─────────────────────────────────────────────────────────────────────────────
// Build the knowledge base (download/read → parse → embed → index)
// ─────────────────────────────────────────────────────────────────────────────

var sources = settings.Knowledge.Sources;
if (sources.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[Config] No Knowledge:Sources configured — the knowledge base will be empty.");
    Console.ResetColor();
}

Console.WriteLine("\n[Init] Building knowledge base...");
foreach (var s in sources)
    Console.WriteLine($"  • {s.Name}");

var documents = await documentLoader.LoadAllAsync(sources, aiService);
vectorStore.AddDocuments(documents);
Console.WriteLine($"[Init] Knowledge base ready — {vectorStore.DocumentCount} chunks indexed.");

// ─────────────────────────────────────────────────────────────────────────────
// Build the workflow graph
// ─────────────────────────────────────────────────────────────────────────────

var kbState = new KnowledgeBaseState();
if (documents.Count > 0)
{
    kbState.Description = sources.Count > 0
        ? string.Join(", ", sources.Select(s =>
            string.IsNullOrWhiteSpace(s.Description) ? s.Name : $"{s.Name} ({s.Description})"))
        : "(no internal documents configured)";
    kbState.HasSource = true;
}

// ─────────────────────────────────────────────────────────────────────────────
// Select pipeline mode
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\nSelect pipeline:");
Console.WriteLine("  [1] One-Shot RAG       (single-pass, linear)");
Console.WriteLine("  [2] Deep-Thinking RAG  (multi-hop, agentic loop)");
Console.Write("> ");

var modeInput = Console.ReadLine()?.Trim();
var useOneShot = modeInput != "2";

var pipelineLabel = useOneShot ? "One-Shot RAG" : "Deep-Thinking RAG";

var workflow = useOneShot
    ? OneShotRagWorkflow.Build(aiService, vectorStore, tavilyService, documentLoader, kbState, settings.Pipeline)
    : AgenticRagWorkflow.Build(aiService, vectorStore, tavilyService, documentLoader, kbState, settings.Pipeline);

Console.WriteLine($"[Pipeline] Selected: {pipelineLabel}");

// ─────────────────────────────────────────────────────────────────────────────
// Determine the query
//   Priority: CLI args > DefaultQuery in config > interactive prompt
// ─────────────────────────────────────────────────────────────────────────────

string query;
if (args.Length > 0)
{
    query = string.Join(" ", args);
}
else if (!string.IsNullOrWhiteSpace(settings.Knowledge.DefaultQuery))
{
    query = settings.Knowledge.DefaultQuery;
    Console.WriteLine($"\n[Query] Using DefaultQuery from config:");
}
else
{
    Console.WriteLine("\nEnter your research question (multi-hop queries work best):");
    Console.Write("> ");
    query = Console.ReadLine() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(query))
    {
        Console.WriteLine("[Error] No query provided. Exiting.");
        return;
    }
}

Console.WriteLine($"\n[Query] {query}");

// ─────────────────────────────────────────────────────────────────────────────
// Run the workflow
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n[Pipeline] Starting {pipelineLabel} pipeline...\n");

var startTime = DateTimeOffset.UtcNow;

await using var run = await InProcessExecution.RunStreamingAsync(
    workflow,
    query);

string? finalAnswer = null;

await foreach (var evt in run.WatchStreamAsync())
{
    if (evt is WorkflowOutputEvent outputEvent && outputEvent.Data is AnswerResult ar)
    {
        finalAnswer = ar.Text;
        break;
    }
}

var elapsed = DateTimeOffset.UtcNow - startTime;

// ─────────────────────────────────────────────────────────────────────────────
// Summary
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n[Done] Workflow completed in {elapsed.TotalSeconds:F1}s");

if (finalAnswer is null)
    Console.WriteLine("[Warning] No output was yielded by the workflow.");

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

static void ValidateSettings(AppSettings s)
{
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(s.AzureAI.Endpoint))
        errors.Add("AzureAI:Endpoint is not set in appsettings.json");

    if (string.IsNullOrWhiteSpace(s.AzureAI.ApiKey))
        errors.Add("AzureAI:ApiKey is not set — set it or configure DefaultAzureCredential");

    if (string.IsNullOrWhiteSpace(s.Tavily.ApiKey))
        errors.Add("Tavily:ApiKey is not set in appsettings.json");

    if (errors.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[Config] Missing configuration values:");
        foreach (var e in errors) Console.WriteLine($"  • {e}");
        Console.ResetColor();
        Console.WriteLine();
        // Don't abort — let the user see all missing fields and proceed if they want.
    }
}

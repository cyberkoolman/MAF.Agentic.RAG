# Agentic RAG Application Construction and Workflow Components

This reference follows the application from the outside in:

1. DevUI starts the web application and constructs shared services.
2. The chat client loads a source through `DocumentLoader`.
3. Embedded chunks are added to the shared `VectorStore`.
4. The agentic workflow is constructed from executor nodes and typed edges.
5. A user query moves through each agent until `SynthesisExecutor` returns the answer.

## Application Construction Map

| Construction stage | Primary file | Referenced code |
|---|---|---|
| Start the ASP.NET Core host and DevUI | `AgenticRAG.Api/Program.cs` | [Lines 15-67](AgenticRAG.Api/Program.cs#L15-L67) |
| Create shared AI, retrieval, loading, and knowledge-base services | `AgenticRAG.Api/Program.cs` | [Lines 27-38](AgenticRAG.Api/Program.cs#L27-L38) |
| Adapt the workflow to the DevUI `IChatClient` contract | `AgenticRAG.Api/RagWorkflowChatClient.cs` | [Lines 23-70](AgenticRAG.Api/RagWorkflowChatClient.cs#L23-L70) |
| Load a source and populate the vector store | `AgenticRAG.Api/RagWorkflowChatClient.cs` | [Lines 187-218](AgenticRAG.Api/RagWorkflowChatClient.cs#L187-L218) |
| Fetch, chunk, and embed source content | `AgenticRAG/Services/DocumentLoader.cs` | [Lines 32-218](AgenticRAG/Services/DocumentLoader.cs#L32-L218) |
| Store chunks and provide retrieval algorithms | `AgenticRAG/Services/VectorStore.cs` | [Lines 15-116](AgenticRAG/Services/VectorStore.cs#L15-L116) |
| Instantiate agents and define the workflow graph | `AgenticRAG/Workflow/AgenticRagWorkflow.cs` | [Lines 45-111](AgenticRAG/Workflow/AgenticRagWorkflow.cs#L45-L111) |

## 1. DevUI Application Startup

### 1.1 Create the web host and load configuration

**File:** [`AgenticRAG.Api/Program.cs`](AgenticRAG.Api/Program.cs)
**Code:** [Host and configuration, lines 15-27](AgenticRAG.Api/Program.cs#L15-L27)

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json",       optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true,  reloadOnChange: false);

builder.Services.AddHttpClient().AddLogging();

var settings = builder.Configuration.Get<AppSettings>()
    ?? throw new InvalidOperationException("Failed to load appsettings.");
```

The API host is the composition root. It creates the application-wide services and passes the same instances to both the agentic and one-shot chat clients.

### 1.2 Construct shared RAG services

**File:** [`AgenticRAG.Api/Program.cs`](AgenticRAG.Api/Program.cs)
**Code:** [Shared service construction, lines 29-38](AgenticRAG.Api/Program.cs#L29-L38)

```csharp
var ai      = new AzureAIService(settings);
var store   = new VectorStore();
var tavily  = new TavilyService(settings);
var loader  = new DocumentLoader(settings);
var kbState = new KnowledgeBaseState();

var agenticClient = new RagWorkflowChatClient(
    settings, ai, store, tavily, loader, kbState, useOneShot: false);

var oneShotClient = new RagWorkflowChatClient(
    settings, ai, store, tavily, loader, kbState, useOneShot: true);
```

Both clients share:

| Shared instance | Purpose |
|---|---|
| `AzureAIService` | Reasoning, fast-model, and embedding calls |
| `VectorStore` | In-memory index of embedded document chunks |
| `TavilyService` | Live web retrieval |
| `DocumentLoader` | Source fetching, chunking, and embedding |
| `KnowledgeBaseState` | Tracks whether sources exist and describes them to the planner |

Because the `VectorStore` and `KnowledgeBaseState` instances are shared, a document loaded from either DevUI agent becomes available to both pipelines.

`KnowledgeBaseState` is application-level source metadata: it tracks whether sources exist and describes them to the gateway and planner. It does not hold the document chunks or a question's research progress. `VectorStore` holds the indexed chunks, while the planner creates a separate `RagState` inside `IWorkflowContext` for each agentic workflow run.

### 1.3 Register the agents and map DevUI

**File:** [`AgenticRAG.Api/Program.cs`](AgenticRAG.Api/Program.cs)
**Code:** [Agent registration, lines 40-52](AgenticRAG.Api/Program.cs#L40-L52); [DevUI registration and endpoint mapping, lines 54-67](AgenticRAG.Api/Program.cs#L54-L67)

`AddAIAgent` registers one user-visible assistant, such as `"Agentic-RAG"`, and exposes it to DevUI through the agent/OpenAI-compatible endpoints. Its implementation is the supplied `IChatClient`; in this application, that client is `RagWorkflowChatClient`, which wraps the complete RAG workflow.

```csharp
builder.AddAIAgent(
    name:         "Agentic-RAG",
    instructions: "You are a deep-thinking research assistant powered by an Agentic RAG pipeline. " +
                  "You decompose complex queries into multi-step plans, retrieve and reflect iteratively, " +
                  "and synthesize comprehensive answers.",
    chatClient:   agenticClient);

builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();
builder.Services.AddDevUI();

WebApplication app = builder.Build();

app.MapOpenAIResponses();
app.MapOpenAIConversations();
app.MapDevUI();

await app.RunAsync();
```

The browser entry point is:

```text
http://localhost:8888/devui
```

## 2. DevUI-to-Workflow Adapter

### 2.1 Construct the workflow-backed chat client

**File:** [`AgenticRAG.Api/RagWorkflowChatClient.cs`](AgenticRAG.Api/RagWorkflowChatClient.cs)
**Code:** [Shared-services constructor, lines 53-70](AgenticRAG.Api/RagWorkflowChatClient.cs#L53-L70)

```csharp
public RagWorkflowChatClient(
    AppSettings        settings,
    AzureAIService     ai,
    VectorStore        store,
    TavilyService      tavily,
    DocumentLoader     loader,
    KnowledgeBaseState kbState,
    bool               useOneShot)
{
    _settings   = settings;
    _ai         = ai;
    _store      = store;
    _tavily     = tavily;
    _loader     = loader;
    _kbState    = kbState;
    _useOneShot = useOneShot;
    _workflow   = BuildWorkflow();
}
```

`RagWorkflowChatClient` implements `IChatClient`, which lets DevUI treat the complete RAG workflow as a chat agent.

### 2.2 Decide whether the user sent a source or a query

**File:** [`AgenticRAG.Api/RagWorkflowChatClient.cs`](AgenticRAG.Api/RagWorkflowChatClient.cs)
**Code:** [Request dispatch, lines 78-111](AgenticRAG.Api/RagWorkflowChatClient.cs#L78-L111)

```csharp
var userText = chatMessages
    .LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Trim() ?? "";

var url = ExtractUrl(userText);
if (url is not null)
{
    var chunks = new List<string>();
    await foreach (var update in LoadSourceAsync(url, cancellationToken))
        if (update.Text is { } t) chunks.Add(t);
    return new ChatResponse(
        [new ChatMessage(ChatRole.Assistant, string.Concat(chunks))]);
}

if (!_kbState.HasSource)
{
    return new ChatResponse([new ChatMessage(
        ChatRole.Assistant,
        "I'm ready! Please share a URL (or paste a file path) to load your " +
        "knowledge source, and I'll index it. Then ask me anything.")]);
}
```

The adapter has two paths:

| Input | Action |
|---|---|
| URL or local file path | Load, chunk, embed, and index the source |
| Plain query with a loaded source | Run the constructed workflow |

### 2.3 Run the workflow

**File:** [`AgenticRAG.Api/RagWorkflowChatClient.cs`](AgenticRAG.Api/RagWorkflowChatClient.cs)
**Code:** [Workflow execution, lines 113-145](AgenticRAG.Api/RagWorkflowChatClient.cs#L113-L145)

`InProcessExecution` is the Microsoft Agent Framework runtime that executes the workflow inside the same ASP.NET Core process as DevUI.

```text
ASP.NET/DevUI process
  └─ InProcessExecution
       └─ Gateway -> Planner -> QueryRewriter -> ... -> Synthesis
```

```csharp
AnswerResult? answerResult = null;
await using var run = await InProcessExecution.RunStreamingAsync(
    _workflow,
    userText);

await foreach (var evt in run.WatchStreamAsync())
{
    if (evt is WorkflowOutputEvent outputEvent &&
        outputEvent.Data is AnswerResult ar)
    {
        answerResult = ar;
        break;
    }
}
```

The workflow receives the user's plain text as its root input. The terminal synthesis agent yields an `AnswerResult`, which the adapter converts back into a DevUI chat response.

## 3. Document Loader and Vector Store Construction

The DevUI application starts with an empty in-memory `VectorStore`. The store becomes useful only after the chat client runs the source-ingestion path.

### 3.1 Create a source and invoke the loader

**File:** [`AgenticRAG.Api/RagWorkflowChatClient.cs`](AgenticRAG.Api/RagWorkflowChatClient.cs)
**Code:** [Source loading, lines 187-218](AgenticRAG.Api/RagWorkflowChatClient.cs#L187-L218)

`DocumentSource` supports **two documented content types**, defined in [`AppSettings.cs`, lines 68-83](AgenticRAG/Configuration/AppSettings.cs#L68-L83):

| `Type` value | Loader behavior | Typical source |
|---|---|---|
| `"html"` | Uses `ChunkHtml`, removes non-content HTML nodes, detects section headings, and preserves section metadata | Web pages and HTML files |
| `"text"` | Uses `ChunkPlainText` and treats the source as unformatted text | `.txt`, Markdown, logs, or other plain-text files |

The content type is separate from the source location. `DocumentSource` can read from either `Url` or `FilePath`. The current DevUI path sets `Type = "html"` for every submitted source; code or configuration loading a plain-text source should set `Type = "text"`. In the current loader, any value other than `"text"` follows the HTML path ([`DocumentLoader.cs`, lines 59-63](AgenticRAG/Services/DocumentLoader.cs#L59-L63)).

PDF and DOCX files need an additional **text-extraction step** because `File.ReadAllTextAsync` cannot decode these binary formats. A practical extension is to add `"pdf"` and `"docx"` type values, extract their text with a format-specific library, then reuse the existing plain-text chunking, embedding, and indexing path.

```csharp
// Illustrative extension inside DocumentLoader.LoadSourceAsync.
// Example libraries: PdfPig for PDF and DocumentFormat.OpenXml for DOCX.
string extractedText = source.Type.ToLowerInvariant() switch
{
    "pdf"  => await ExtractPdfTextAsync(source, cancellationToken),
    "docx" => await ExtractDocxTextAsync(source, cancellationToken),
    _      => await FetchContentAsync(source, cancellationToken)
};

var chunks = source.Type.ToLowerInvariant() switch
{
    "html" => ChunkHtml(extractedText, source.Name),
    _      => ChunkPlainText(extractedText, source.Name)
};

return await EmbedChunksAsync(
    chunks,
    source,
    aiService,
    cancellationToken);
```

The extraction helpers could follow this general shape:

```csharp
private static Task<string> ExtractPdfTextAsync(
    DocumentSource source,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    using var pdf = UglyToad.PdfPig.PdfDocument.Open(source.FilePath);

    var text = string.Join(
        "\n\n",
        pdf.GetPages().Select(page =>
            $"[Page {page.Number}]\n{page.Text}"));

    return Task.FromResult(text);
}

private static Task<string> ExtractDocxTextAsync(
    DocumentSource source,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    using var doc = WordprocessingDocument.Open(
        source.FilePath,
        isEditable: false);

    var text = doc.MainDocumentPart?
        .Document.Body?
        .InnerText ?? "";

    return Task.FromResult(text);
}
```

For remote PDF or DOCX URLs, download the content as bytes or a stream rather than calling `GetStringAsync`. Production extraction should also preserve useful metadata such as PDF page number or DOCX heading so each resulting `RagDocument.Section` can produce precise citations. The sample is architectural only; the required parser packages are not currently included in this repository.

```csharp
var source = new DocumentSource
{
    Name     = UrlToName(url),
    Url      = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "",
    FilePath = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "" : url,
    Type     = "html",
};

var docs = await _loader.LoadAllAsync([source], _ai, ct);
_store.AddDocuments(docs);
_kbState.AppendSource(source.Name);

_workflow = BuildWorkflow();
```

The construction sequence is:

```text
DevUI message
  -> RagWorkflowChatClient.LoadSourceAsync
  -> DocumentSource
  -> DocumentLoader.LoadAllAsync
  -> fetch source content
  -> create overlapping chunks with section metadata
  -> generate embedding for each chunk
  -> List<RagDocument>
  -> VectorStore.AddDocuments
  -> update KnowledgeBaseState
  -> rebuild workflow with the same shared services
```

The `_lock` acquired inside `LoadSourceAsync()` prevents concurrent source-loading requests handled by the same `RagWorkflowChatClient` instance from mutating the store and rebuilding its workflow simultaneously. The intent is to prevent two source-building operations from updating the same client state at the same time.

### 3.2 Load every requested source

**File:** [`AgenticRAG/Services/DocumentLoader.cs`](AgenticRAG/Services/DocumentLoader.cs)
**Code:** [Loading entry point, lines 32-48](AgenticRAG/Services/DocumentLoader.cs#L32-L48)

For an enterprise with multiple document sources—such as policies, product manuals, contracts, architecture standards, and support procedures—`LoadAllAsync` provides one batch-ingestion entry point. Each source follows the same loading, chunking, metadata, and embedding process, and the resulting chunks are combined into one searchable knowledge base.

The current implementation processes sources sequentially and stores the combined result in the in-memory `VectorStore`.

```csharp
public async Task<List<RagDocument>> LoadAllAsync(
    IEnumerable<DocumentSource> sources,
    AzureAIService aiService,
    CancellationToken cancellationToken = default)
{
    var all = new List<RagDocument>();

    foreach (var source in sources)
    {
        var docs = await LoadSourceAsync(
            source,
            aiService,
            cancellationToken);
        all.AddRange(docs);
    }

    return all;
}
```

### 3.3 Fetch, chunk, and embed one source

**File:** [`AgenticRAG/Services/DocumentLoader.cs`](AgenticRAG/Services/DocumentLoader.cs)
**Code:** [Per-source pipeline, lines 54-66](AgenticRAG/Services/DocumentLoader.cs#L54-L66)

`FetchContentAsync` loads one entire source into a `string` in one shot; it does not stream or incrementally parse the content. This is simple and suitable for moderate document sizes, but a very large source temporarily occupies memory as both the complete source string and its generated chunks.

```csharp
string rawContent = await FetchContentAsync(source, cancellationToken);

var chunks = source.Type.ToLowerInvariant() == "text"
    ? ChunkPlainText(rawContent, source.Name)
    : ChunkHtml(rawContent, source.Name);

return await EmbedChunksAsync(
    chunks,
    source,
    aiService,
    cancellationToken);
```

### 3.4 Fetch a local file or remote URL

**File:** [`AgenticRAG/Services/DocumentLoader.cs`](AgenticRAG/Services/DocumentLoader.cs)
**Code:** [Content fetching, lines 73-93](AgenticRAG/Services/DocumentLoader.cs#L73-L93)

```csharp
if (!string.IsNullOrWhiteSpace(source.FilePath))
{
    return await File.ReadAllTextAsync(source.FilePath, ct);
}

if (!string.IsNullOrWhiteSpace(source.Url))
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.TryAddWithoutValidation(
        "User-Agent", "AgenticRAG Research Bot research@example.com");
    return await http.GetStringAsync(source.Url, ct);
}
```

### 3.5 Generate embeddings and construct indexed records

**File:** [`AgenticRAG/Services/DocumentLoader.cs`](AgenticRAG/Services/DocumentLoader.cs)
**Code:** [Batch embedding, lines 188-218](AgenticRAG/Services/DocumentLoader.cs#L188-L218)

Each chunk has two simple parts: `Content` is all the text contained in that chunk, and `Section` is the label describing where that text came from in the document, such as `"Item 1A. Risk Factors"`. Keeping both lets the business retrieve the relevant text while also showing users the document area that supports the answer.

```csharp
const int BatchSize = 16;
var documents = new List<RagDocument>(chunks.Count);

for (int i = 0; i < chunks.Count; i += BatchSize)
{
    var batch  = chunks.Skip(i).Take(BatchSize).ToList();
    var embeds = await aiService.GenerateEmbeddingsAsync(
        batch.Select(c => c.Content),
        cancellationToken);

    for (int j = 0; j < batch.Count; j++)
    {
        documents.Add(new RagDocument
        {
            Content   = batch[j].Content,
            Section   = batch[j].Section,
            Source    = source.Name,
            Url       = source.Url,
            Embedding = embeds[j]
        });
    }
}
```

**Embedding implementation:** [`AzureAIService.cs`, lines 141-163](AgenticRAG/Services/AzureAIService.cs#L141-L163)

```csharp
var embeddingClient = _client.GetEmbeddingClient(
    _settings.EmbeddingModel);
var textList = texts.ToList();
var response = await embeddingClient.GenerateEmbeddingsAsync(
    textList,
    cancellationToken: cancellationToken);

return response.Value
    .Select(e => e.ToFloats().ToArray())
    .ToList();
```

### 3.6 Search Strategy

**File:** [`AgenticRAG/Executors/VectorSearchExecutor.cs`](AgenticRAG/Executors/VectorSearchExecutor.cs)
**Code:** [Strategy selection, lines 70-96](AgenticRAG/Executors/VectorSearchExecutor.cs#L70-L96)

Depending on what the user asks and how the question is phrased, `ChooseStrategyAsync` asks the fast LLM to select one search method for that request: vector, keyword, or hybrid. Both one-shot and agentic internal-document searches use this same strategy supervisor.

```csharp
private async Task<SearchStrategy> ChooseStrategyAsync(
    string query,
    CancellationToken ct)
{
    const string SystemPrompt = """
        You are a retrieval strategy supervisor for a document search engine.

        Choose the single best search strategy for the query:
          • vector   — open-ended conceptual, thematic, or analytical questions where meaning
                       matters more than exact words
          • keyword  — queries that are PRIMARILY composed of exact identifiers with no
                       analytical component: specific product model numbers, version strings,
                       financial figures, exact quoted phrases, or precise section titles
          • hybrid   — queries that mix a conceptual question with specific named entities
                       or domain terms

        Note: an entity or subject name alone is NOT sufficient to choose keyword.
        Default to hybrid when in doubt.

        Respond with exactly one word: vector, keyword, or hybrid.
        """;

    var response = (await _ai.CompleteAsync(
        SystemPrompt,
        $"Query: {query}",
        useReasoningModel: false,
        ct)).Text;

    return response.Trim().ToLowerInvariant() switch
    {
        "keyword" => SearchStrategy.Keyword,
        "vector"  => SearchStrategy.Vector,
        _         => SearchStrategy.Hybrid
    };
}
```

| Search method | When selected | Example user ask | Implementation |
|---|---|---|---|
| `VectorSearch` | Conceptual, thematic, or paraphrased questions where meaning matters more than exact wording | “What are the main business risks?” | [Cosine similarity, lines 34-43](AgenticRAG/Services/VectorStore.cs#L34-L43) |
| `KeywordSearch` | Exact identifiers, figures, quoted phrases, model numbers, or section titles | “Find Item 1A” | [Term-overlap ranking, lines 48-65](AgenticRAG/Services/VectorStore.cs#L48-L65) |
| `HybridSearch` | Conceptual questions that also contain specific names or domain terms; this is the fallback when classification is unclear | “What risks affect NVIDIA H100 exports?” | [Reciprocal Rank Fusion, lines 71-94](AgenticRAG/Services/VectorStore.cs#L71-L94) |

## 4. Workflow Definition

### 4.1 Select and construct the workflow

**File:** [`AgenticRAG.Api/RagWorkflowChatClient.cs`](AgenticRAG.Api/RagWorkflowChatClient.cs)
**Code:** [Workflow factory, lines 225-228](AgenticRAG.Api/RagWorkflowChatClient.cs#L225-L228)

```csharp
private Microsoft.Agents.AI.Workflows.Workflow BuildWorkflow() =>
    _useOneShot
        ? OneShotRagWorkflow.Build(
            _ai, _store, _tavily, _loader, _kbState, _settings.Pipeline)
        : AgenticRagWorkflow.Build(
            _ai, _store, _tavily, _loader, _kbState, _settings.Pipeline);
```

For the `Agentic-RAG` DevUI agent, `_useOneShot` is `false`, so the factory calls `AgenticRagWorkflow.Build`.

### 4.2 Instantiate every agent node

**File:** [`AgenticRAG/Workflow/AgenticRagWorkflow.cs`](AgenticRAG/Workflow/AgenticRagWorkflow.cs)
**Code:** [Agent construction, lines 45-65](AgenticRAG/Workflow/AgenticRagWorkflow.cs#L45-L65)

```csharp
var gateway       = new GatewayExecutor(
    aiService, vectorStore, documentLoader, kbState);
var planner       = new PlannerExecutor(aiService, kbState);
var queryRewriter = new QueryRewriterExecutor(aiService);
var vectorSearch  = new VectorSearchExecutor(
    aiService,
    vectorStore,
    pipelineSettings.InitialRetrievalTopK);
var webSearch     = new WebSearchExecutor(tavilyService);
var reranker      = new RerankerExecutor(
    aiService,
    pipelineSettings.RerankerTopK);
var distiller     = new DistillerExecutor(aiService);
var reflection    = new ReflectionExecutor(aiService);
var policy        = new PolicyExecutor(aiService);
var synthesis     = new SynthesisExecutor(aiService);
```

### 4.3 Connect the typed workflow edges

**File:** [`AgenticRAG/Workflow/AgenticRagWorkflow.cs`](AgenticRAG/Workflow/AgenticRagWorkflow.cs)
**Code:** [Graph edges, lines 69-108](AgenticRAG/Workflow/AgenticRagWorkflow.cs#L69-L108)

```csharp
var workflowBuilder = new WorkflowBuilder(gateway)
    .AddEdge<UserQuery>(gateway, planner, condition: _ => true)
    .AddEdge(planner, queryRewriter)
    .AddEdge<SearchRequest>(
        queryRewriter,
        vectorSearch,
        condition: msg => msg?.Tool == "search_docs")
    .AddEdge<SearchRequest>(
        queryRewriter,
        webSearch,
        condition: msg => msg?.Tool == "search_web")
    .AddEdge(vectorSearch, reranker)
    .AddEdge(webSearch, reranker)
    .AddEdge(reranker, distiller)
    .AddEdge(distiller, reflection)
    .AddEdge(reflection, policy)
    .AddEdge<StepSignal>(
        policy,
        queryRewriter,
        condition: _ => true)
    .AddEdge<FinishSignal>(
        policy,
        synthesis,
        condition: _ => true)
    .WithOutputFrom(synthesis);
```

The graph is cyclical:

```text
Gateway
  -> Planner
  -> Query Rewriter
  -> Vector Search or Web Search
  -> Reranker
  -> Distiller
  -> Reflection
  -> Policy
       -> CONTINUE: Query Rewriter
       -> FINISH: Synthesis
```

## 5. Agent-by-Agent Workflow Steps

| Step | Agent | Receives | Sends or yields | Model tier |
|---|---|---|---|---|
| 0 | Gateway | `string` | `UserQuery` or direct response | None for query routing |
| 1 | Planner | `UserQuery` | `StepSignal(0)` | Reasoning |
| 2 | Query Rewriter | `StepSignal` | `SearchRequest` | Fast |
| 3A | Vector Search | `SearchRequest` | `SearchResults` | Fast + embedding |
| 3B | Web Search | `SearchRequest` | `SearchResults` | External Tavily API |
| 4 | Reranker | `SearchResults` | `RankedResults` | Fast |
| 5 | Distiller | `RankedResults` | `DistilledContext` | Fast |
| 6 | Reflection | `DistilledContext` | `PolicySignal` | Fast |
| 7 | Policy | `PolicySignal` | `StepSignal` or `FinishSignal` | Reasoning |
| 8 | Synthesis | `FinishSignal` | `AnswerResult` | Reasoning |

### Step 0: Gateway

**File:** [`AgenticRAG/Executors/GatewayExecutor.cs`](AgenticRAG/Executors/GatewayExecutor.cs)
**Code:** [Gateway handler, lines 38-84](AgenticRAG/Executors/GatewayExecutor.cs#L38-L84)

In DevUI, `RagWorkflowChatClient` normally intercepts URLs before starting the workflow. For a plain question, the gateway confirms that a source exists and converts the root string into `UserQuery`.

```csharp
if (!_kbState.HasSource)
{
    await context.YieldOutputAsync(
        "I'm ready! Please share a URL (or paste a file path) to load your " +
        "knowledge source, and I'll index it. Then ask me anything.",
        ct);
    return;
}

await context.SendMessageAsync(new UserQuery(text), ct);
```

### Step 1: Planner

**File:** [`AgenticRAG/Executors/PlannerExecutor.cs`](AgenticRAG/Executors/PlannerExecutor.cs)
**Code:** [Planning and state creation, lines 34-100](AgenticRAG/Executors/PlannerExecutor.cs#L34-L100)

The planner decomposes the request into two to five ordered steps. Every step chooses `search_docs` or `search_web`.

```csharp
var plan = await _ai.CompleteStructuredAsync<ResearchPlan>(
    systemPrompt,
    $"Create a research plan for:\n\n{message.Query}",
    useReasoningModel: true,
    cancellationToken);

var state = new RagState
{
    UserQuery        = message.Query,
    Plan             = steps,
    CurrentStepIndex = 0,
    IterationCount   = 0
};

await context.QueueStateUpdateAsync(
    StateKey,
    state,
    scopeName: StateScope,
    cancellationToken);

await context.SendMessageAsync(
    new StepSignal(0),
    cancellationToken: cancellationToken);
```

### Step 2: Query Rewriter

**File:** [`AgenticRAG/Executors/QueryRewriterExecutor.cs`](AgenticRAG/Executors/QueryRewriterExecutor.cs)
**Code:** [State-aware rewriting, lines 33-114](AgenticRAG/Executors/QueryRewriterExecutor.cs#L33-L114)

The rewriter reads the selected plan step and previous findings. It optimizes the query for internal retrieval or live web search, then emits the typed routing message.

```csharp
var step = state.Plan[message.StepIndex];

var historyContext = state.ResearchHistory.Count > 0
    ? $"\n\nPrevious findings:\n{string.Join("\n", state.ResearchHistory)}"
    : string.Empty;

// The first internal step uses the original query. Later steps use the
// fast model to target what remains unknown.

await context.SendMessageAsync(
    new SearchRequest(
        rewritten,
        step.Tool,
        step.SubQuestion,
        message.StepIndex),
    cancellationToken: cancellationToken);
```

### Step 3A: Vector Search

**File:** [`AgenticRAG/Executors/VectorSearchExecutor.cs`](AgenticRAG/Executors/VectorSearchExecutor.cs)
**Code:** [Strategy selection and retrieval, lines 33-65](AgenticRAG/Executors/VectorSearchExecutor.cs#L33-L65); [retrieval supervisor, lines 70-96](AgenticRAG/Executors/VectorSearchExecutor.cs#L70-L96)

The retrieval supervisor chooses vector, keyword, or hybrid search. Vector and hybrid paths create a query embedding compatible with the stored document embeddings.

```csharp
var strategy = await ChooseStrategyAsync(
    message.RewrittenQuery,
    cancellationToken);

if (strategy == SearchStrategy.Vector ||
    strategy == SearchStrategy.Hybrid)
{
    var embedding = await _ai.GenerateEmbeddingAsync(
        message.RewrittenQuery,
        cancellationToken);

    results = strategy == SearchStrategy.Hybrid
        ? _store.HybridSearch(
            message.RewrittenQuery,
            embedding,
            _topK)
        : _store.VectorSearch(embedding, _topK);
}
else
{
    results = _store.KeywordSearch(
        message.RewrittenQuery,
        _topK);
}
```

### Step 3B: Web Search

**File:** [`AgenticRAG/Executors/WebSearchExecutor.cs`](AgenticRAG/Executors/WebSearchExecutor.cs)
**Code:** [Tavily retrieval, lines 24-43](AgenticRAG/Executors/WebSearchExecutor.cs#L24-L43)

```csharp
var results = await _tavily.SearchAsync(
    message.RewrittenQuery,
    maxResults: 5,
    cancellationToken);

await context.SendMessageAsync(
    new SearchResults(
        results,
        message.OriginalSubQuestion,
        message.StepIndex),
    cancellationToken: cancellationToken);
```

Tavily results are converted into `RagDocument` records in [`TavilyService.cs`, lines 29-61](AgenticRAG/Services/TavilyService.cs#L29-L61), allowing both search branches to use the same downstream agents.

### Step 4: Reranker

**File:** [`AgenticRAG/Executors/RerankerExecutor.cs`](AgenticRAG/Executors/RerankerExecutor.cs)
**Code:** [LLM reranking, lines 31-89](AgenticRAG/Executors/RerankerExecutor.cs#L31-L89)

The reranker receives the broad candidate set and asks the fast model for the most relevant document indices.

```csharp
var response = await _ai.CompleteStructuredAsync<RankerResponse>(
    SystemPrompt,
    $"Question: {message.SubQuestion}\n\n" +
    $"Documents:\n{docList}\n\n" +
    $"Return the {_topK} most relevant indices:",
    useReasoningModel: false,
    cancellationToken);

var topDocs = indices
    .Where(i => i >= 0 && i < message.Documents.Count)
    .Distinct()
    .Take(_topK)
    .Select((i, rank) => message.Documents[i] with
    {
        RelevanceScore = 1f / (rank + 1)
    })
    .ToList();

await context.SendMessageAsync(
    new RankedResults(
        topDocs,
        message.SubQuestion,
        message.StepIndex),
    cancellationToken: cancellationToken);
```

### Step 5: Distiller

**File:** [`AgenticRAG/Executors/DistillerExecutor.cs`](AgenticRAG/Executors/DistillerExecutor.cs)
**Code:** [Context distillation, lines 27-72](AgenticRAG/Executors/DistillerExecutor.cs#L27-L72)

The distiller compresses the reranked chunks into one dense evidence paragraph while preserving facts and citations.

```csharp
var distilled = (await _ai.CompleteAsync(
    SystemPrompt,
    $"Question: {message.SubQuestion}\n\n" +
    $"Documents:\n{docs}\n\nDistilled context:",
    useReasoningModel: false,
    cancellationToken)).Text;

await context.SendMessageAsync(
    new DistilledContext(
        distilled.Trim(),
        message.SubQuestion,
        message.StepIndex),
    cancellationToken: cancellationToken);
```

### Step 6: Reflection

**File:** [`AgenticRAG/Executors/ReflectionExecutor.cs`](AgenticRAG/Executors/ReflectionExecutor.cs)
**Code:** [Research-state update, lines 24-70](AgenticRAG/Executors/ReflectionExecutor.cs#L24-L70)

Reflection writes both a concise finding and the full distilled evidence into shared `RagState`.

```csharp
var stepLabel =
    $"Step {message.StepIndex + 1} " +
    $"[{state.Plan[message.StepIndex].Tool}]: {summary}.";

state.ResearchHistory.Add(stepLabel);
state.DistilledContexts.Add(
    $"=== Step {message.StepIndex + 1}: " +
    $"{message.SubQuestion} ===\n{message.Context}");

await context.QueueStateUpdateAsync(
    PlannerExecutor.StateKey,
    state,
    scopeName: PlannerExecutor.StateScope,
    cancellationToken);

await context.SendMessageAsync(
    new PolicySignal(message.StepIndex),
    cancellationToken: cancellationToken);
```

### Step 7: Policy

**File:** [`AgenticRAG/Executors/PolicyExecutor.cs`](AgenticRAG/Executors/PolicyExecutor.cs)
**Code:** [Hard stops and policy decision, lines 32-102](AgenticRAG/Executors/PolicyExecutor.cs#L32-L102)

The policy agent controls the loop. It first applies deterministic safety rules, then uses the reasoning model to decide whether more research adds value.

```csharp
if (nextStep >= state.Plan.Count ||
    state.IterationCount >= RagState.MaxIterations)
{
    await context.SendMessageAsync(
        new FinishSignal(),
        cancellationToken: cancellationToken);
    return;
}

var decision = await _ai.CompleteStructuredAsync<PolicyDecision>(
    SystemPrompt,
    $"Original query:\n{state.UserQuery}\n\n" +
    $"Plan:\n{planSummary}\n\n" +
    $"Research history:\n{history}",
    useReasoningModel: true,
    cancellationToken);

if (action == "FINISH")
    await context.SendMessageAsync(
        new FinishSignal(),
        cancellationToken: cancellationToken);
else
    await context.SendMessageAsync(
        new StepSignal(nextStep),
        cancellationToken: cancellationToken);
```

### Step 8: Synthesis

**File:** [`AgenticRAG/Executors/SynthesisExecutor.cs`](AgenticRAG/Executors/SynthesisExecutor.cs)
**Code:** [Final answer generation and workflow output, lines 24-83](AgenticRAG/Executors/SynthesisExecutor.cs#L24-L83)

The synthesis agent reads all accumulated evidence, generates the final answer with the reasoning model, and terminates the workflow.

```csharp
var evidenceBlock = state.DistilledContexts.Count > 0
    ? string.Join("\n\n", state.DistilledContexts)
    : string.Join("\n", state.ResearchHistory);

var result = await _ai.CompleteAsync(
    SystemPrompt,
    $"Original query:\n{state.UserQuery}\n\n" +
    $"Research summary:\n{historySummary}\n\n" +
    $"Detailed evidence:\n{evidenceBlock}\n\n" +
    "Write the comprehensive answer:",
    useReasoningModel: true,
    cancellationToken);

await context.YieldOutputAsync(
    new AnswerResult(
        result.Text,
        result.InputTokens,
        result.OutputTokens,
        result.TotalTokens),
    cancellationToken);
```

## 6. Complete Runtime Sequence

```text
ASP.NET Core starts
  -> construct shared AzureAIService, VectorStore, TavilyService,
     DocumentLoader, and KnowledgeBaseState
  -> construct Agentic RagWorkflowChatClient
  -> AgenticRagWorkflow.Build creates agents and typed edges
  -> register the chat client as "Agentic-RAG"
  -> map DevUI

User supplies a source
  -> RagWorkflowChatClient intercepts URL/file path
  -> DocumentLoader fetches and chunks content
  -> AzureAIService creates document embeddings
  -> VectorStore stores RagDocument chunks
  -> KnowledgeBaseState records the source
  -> chat client rebuilds the workflow

User asks a question
  -> run workflow with the question string
  -> Gateway emits UserQuery
  -> Planner creates plan and RagState
  -> Query Rewriter emits SearchRequest
  -> Vector Search or Web Search emits SearchResults
  -> Reranker emits RankedResults
  -> Distiller emits DistilledContext
  -> Reflection updates RagState and emits PolicySignal
  -> Policy loops with StepSignal or finishes with FinishSignal
  -> Synthesis yields AnswerResult
  -> RagWorkflowChatClient returns the answer to DevUI
```

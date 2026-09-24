# AGENTS.md — Developer Reference

This document is the internal developer guide for the agent executors, message contracts, shared state, and LLM model assignments. For high-level architecture, see [`README.md`](README.md). For pipeline comparison, see [`RAG.Comparison.md`](RAG.Comparison.md).

---

## Message Flow

Every edge in the workflow graph carries a **typed message record**. The Microsoft Agent Framework routes messages by type, so each executor declares what it receives (`Executor<T>`) and what it sends (`[SendsMessage(typeof(T))]`).

### Message Types

| Record | Sent By | Received By | Purpose |
|---|---|---|---|
| `UserQuery(Query)` | Gateway | Planner / QueryBridge | Raw user question entering the pipeline |
| `StepSignal(StepIndex)` | Planner, Policy | QueryRewriter | Triggers processing of a specific plan step |
| `SearchRequest(RewrittenQuery, Tool, OriginalSubQuestion, StepIndex)` | QueryRewriter / QueryBridge | VectorSearch, WebSearch | Routed by `Tool` field: `"search_docs"` or `"search_web"` |
| `SearchResults(Documents, SubQuestion, StepIndex)` | VectorSearch, WebSearch | Reranker | Broad-recall candidate set |
| `RankedResults(TopDocuments, SubQuestion, StepIndex)` | Reranker | Distiller / OneShotAnswer | Precision-filtered top-K documents |
| `DistilledContext(Context, SubQuestion, StepIndex)` | Distiller | Reflection | Compressed evidence paragraph |
| `PolicySignal(CompletedStepIndex)` | Reflection | Policy | Triggers control-flow decision |
| `FinishSignal()` | Policy | Synthesis | Terminates the loop, triggers final answer |

### Conditional Routing

Two edges use **conditional routing** based on message content:

```
QueryRewriter → VectorSearch   when SearchRequest.Tool == "search_docs"
QueryRewriter → WebSearch      when SearchRequest.Tool == "search_web"

Policy → QueryRewriter         when message is StepSignal (CONTINUE)
Policy → Synthesis             when message is FinishSignal (FINISH)
```

---

## Shared State — `RagState`

All executors in the deep-thinking pipeline share state through `IWorkflowContext` using the scope `"rag"` and key `"state"`.

| Field | Type | Written By | Read By | Purpose |
|---|---|---|---|---|
| `UserQuery` | `string` | Planner | Reflection, Synthesis | Original question for reference |
| `Plan` | `List<ResearchStep>` | Planner | QueryRewriter, Policy, Synthesis | Ordered sub-questions with tool assignments |
| `CurrentStepIndex` | `int` | QueryRewriter | QueryRewriter | Currently processing step (0-based) |
| `ResearchHistory` | `List<string>` | Reflection | Policy, Synthesis | One-sentence summary per completed step |
| `DistilledContexts` | `List<string>` | Reflection | Synthesis | Full distilled evidence per step |
| `IterationCount` | `int` | QueryRewriter | Policy | Safety counter (hard cap: `MaxIterations = 10`) |

**Note:** The one-shot pipeline does **not** use `RagState` — it is stateless by design.

---

## Executor Reference

### Gateway — `GatewayExecutor`

| | |
|---|---|
| **Receives** | `string` (raw workflow input) |
| **Sends** | `UserQuery` · or yields output directly |
| **LLM model** | None |
| **Used in** | Both pipelines |

Triages every incoming message:
- **URL or file path detected** → loads, chunks, embeds, indexes the document; yields a confirmation message.
- **No source loaded** → yields a prompt asking for a URL.
- **Has source + plain query** → forwards as `UserQuery`.

---

### Planner — `PlannerExecutor`

| | |
|---|---|
| **Receives** | `UserQuery` |
| **Sends** | `StepSignal(0)` |
| **LLM model** | Reasoning (e.g. `gpt-4o`) |
| **Used in** | Deep-thinking only |

Decomposes the user query into 2–5 ordered sub-questions. Each step declares a tool (`search_docs` or `search_web`). The knowledge base description is injected so the planner knows what documents are available.

**Output format:** JSON `{ "steps": [{ "subQuestion", "reasoning", "tool" }] }`

**Fallback:** If the LLM returns no steps, a single `search_docs` step with the raw query is added.

---

### QueryRewriter — `QueryRewriterExecutor`

| | |
|---|---|
| **Receives** | `StepSignal` |
| **Sends** | `SearchRequest` |
| **LLM model** | Fast (e.g. `gpt-4o-mini`) |
| **Used in** | Deep-thinking only |

Reads the current step from `RagState.Plan`, incorporates previous findings from `ResearchHistory`, and rewrites the sub-question into an optimised search query for the target tool.

---

### QueryBridge — `QueryBridgeExecutor`

| | |
|---|---|
| **Receives** | `UserQuery` |
| **Sends** | `SearchRequest` |
| **LLM model** | None |
| **Used in** | One-shot only |

Trivial mapper: passes the raw query text straight to `VectorSearchExecutor` as a `SearchRequest` with `Tool = "search_docs"`. No rewriting, no LLM call. This is what makes one-shot retrieval naive — the user's exact words become the search query.

**Known limitation:** User queries are typically generic ("what are the risk factors?") while document chunks contain specific vocabulary ("geopolitical export restrictions", "customer concentration"). The embedding distance between a generic question and specific content is often poor. The Query Rewriter in the agentic pipeline addresses this by generating targeted queries aligned to the document's actual language. As a result, One-Shot can miss entire categories of content even when the source document fully covers the topic — the issue is vocabulary mismatch, not missing data.

---

### VectorSearch — `VectorSearchExecutor`

| | |
|---|---|
| **Receives** | `SearchRequest` |
| **Sends** | `SearchResults` |
| **LLM model** | Fast (strategy selection) + Embedding model |
| **Used in** | Both pipelines |

Contains an internal **Retrieval Supervisor** that selects the best search strategy:

| Strategy | When Selected |
|---|---|
| `vector` | Conceptual, thematic, or paraphrased questions |
| `keyword` | Queries with exact names, acronyms, numbers, quoted phrases |
| `hybrid` | Queries benefiting from both precision and semantic recall |

Returns `InitialRetrievalTopK` (default: 10) candidate documents.

---

### WebSearch — `WebSearchExecutor`

| | |
|---|---|
| **Receives** | `SearchRequest` |
| **Sends** | `SearchResults` |
| **LLM model** | None (external API) |
| **External service** | Tavily Search API |
| **Used in** | Deep-thinking only |

Fetches up to 5 live web results. Results are wrapped as `RagDocument` records so they flow through the same Reranker → Distiller pipeline as internal documents.

---

### Reranker — `RerankerExecutor`

| | |
|---|---|
| **Receives** | `SearchResults` |
| **Sends** | `RankedResults` |
| **LLM model** | Fast (e.g. `gpt-4o-mini`) |
| **Used in** | Both pipelines |

Uses the fast LLM as a **cross-encoder proxy**: scores each document's relevance to the sub-question and returns the top-K (default: 3) most relevant.

**Output format:** JSON `{ "ranked_indices": [2, 0, 5] }`

**Fallback:** If parsing fails, takes the first K documents in original order.

---

### Distiller — `DistillerExecutor`

| | |
|---|---|
| **Receives** | `RankedResults` |
| **Sends** | `DistilledContext` |
| **LLM model** | Fast (e.g. `gpt-4o-mini`) |
| **Used in** | Deep-thinking only |

Compresses the top-3 reranked documents into a single dense paragraph (≤300 words). Preserves exact facts, numbers, and inline citations. Removes redundancy across overlapping chunks.

---

### Reflection — `ReflectionExecutor`

| | |
|---|---|
| **Receives** | `DistilledContext` |
| **Sends** | `PolicySignal` |
| **LLM model** | Fast (e.g. `gpt-4o-mini`) |
| **Used in** | Deep-thinking only |

Summarises the distilled context into a single factual sentence and appends it to `RagState.ResearchHistory`. Also stores the full distilled text in `RagState.DistilledContexts` for the Synthesis agent.

---

### Policy — `PolicyExecutor`

| | |
|---|---|
| **Receives** | `PolicySignal` |
| **Sends** | `StepSignal` (CONTINUE) · or `FinishSignal` (FINISH) |
| **LLM model** | Reasoning (e.g. `gpt-4o`) |
| **Used in** | Deep-thinking only |

The control-flow decision maker. Hard-stop rules (checked before LLM call):
- All plan steps exhausted → `FINISH`
- `MaxIterations` reached → `FINISH`

Otherwise, the LLM evaluates whether accumulated research is sufficient.

**Output format:** JSON `{ "action": "CONTINUE" }` or `{ "action": "FINISH" }`

---

### Synthesis — `SynthesisExecutor`

| | |
|---|---|
| **Receives** | `FinishSignal` |
| **Sends** | Yields workflow output (`string`) |
| **LLM model** | Reasoning (e.g. `gpt-4o`) |
| **Used in** | Deep-thinking only |

Reads the complete `RagState` — all distilled contexts and research history — and produces a comprehensive, multi-hop answer with inline citations. This is the terminal node; it calls `YieldOutputAsync` to end the workflow.

---

### OneShotAnswer — `OneShotAnswerExecutor`

| | |
|---|---|
| **Receives** | `RankedResults` |
| **Sends** | Yields workflow output (`string`) |
| **LLM model** | Reasoning (e.g. `gpt-4o`) |
| **Used in** | One-shot only |

Receives the reranked documents and generates an answer in a single LLM call. No distillation, no reflection, no accumulated memory. The raw reranked chunks are concatenated directly into the prompt.

---

## LLM Model Assignments

The pipeline uses a **dual LLM strategy** — a powerful model for high-stakes decisions and a fast/cheap model for routine work.

| Model tier | Config key | Default | Used by |
|---|---|---|---|
| **Reasoning** | `AzureAI:ReasoningModel` | `gpt-4o` | Planner, Policy, Synthesis, OneShotAnswer |
| **Fast** | `AzureAI:FastModel` | `gpt-4o-mini` | QueryRewriter, VectorSearch (strategy), Reranker, Distiller, Reflection |
| **Embedding** | `AzureAI:EmbeddingModel` | `text-embedding-3-small` | VectorSearch (query embedding), DocumentLoader (chunk embedding) |

---

## Data Models

### `RagDocument`

Represents a single chunk in the knowledge base or a web search result.

| Field | Type | Description |
|---|---|---|
| `Id` | `string` | Unique chunk identifier (GUID) |
| `Content` | `string` | Text content of the chunk |
| `Source` | `string` | Human-readable source name (e.g. "NVIDIA 2024 10-K") |
| `Section` | `string` | Section metadata (e.g. "Item 1A. Risk Factors") |
| `Embedding` | `float[]` | Vector embedding for semantic search |
| `RelevanceScore` | `float` | Score assigned during retrieval or reranking |
| `Url` | `string` | URL for inline citation links |

### `ResearchPlan` / `ResearchStep`

| Field | Type | Description |
|---|---|---|
| `Steps` | `List<ResearchStep>` | Ordered list of research steps |
| `Step.SubQuestion` | `string` | The specific sub-question this step answers |
| `Step.Reasoning` | `string` | Why this step is needed |
| `Step.Tool` | `string` | `"search_docs"` or `"search_web"` |

### `KnowledgeBaseState`

Thread-safe application-shared state tracking loaded sources across workflow runs.

| Field | Type | Description |
|---|---|---|
| `HasSource` | `bool` | Whether any documents have been indexed |
| `Description` | `string` | Comma-separated names of all loaded sources |

`KnowledgeBaseState` does not store document chunks or per-question research. `VectorStore` holds the indexed `RagDocument` chunks, while `RagState` holds one agentic workflow run's plan, findings, evidence, and loop progress.

---

## Pipeline Configuration

All pipeline tuning knobs are in `appsettings.json` under `Pipeline`:

| Key | Default | Effect |
|---|---|---|
| `ChunkSize` | `500` | Approximate token count per document chunk |
| `ChunkOverlap` | `50` | Token overlap between consecutive chunks |
| `InitialRetrievalTopK` | `10` | Documents retrieved before reranking (broad recall) |
| `RerankerTopK` | `3` | Documents kept after reranking (precision) |
| `MaxIterations` | `10` | Hard cap on research loop iterations |

---

## Extending the Pipeline

### Adding a new executor

1. Create a class in `AgenticRAG/Executors/` extending `Executor<TInput>`.
2. Annotate with `[SendsMessage(typeof(TOutput))]` or `[YieldsOutput(typeof(T))]`.
3. Wire it into the workflow graph in `AgenticRagWorkflow.cs` or `OneShotRagWorkflow.cs` using `AddEdge`.

### Adding a new message type

1. Add a `record` to `AgenticRAG/Models/WorkflowMessages.cs`.
2. Use it as the type parameter in `Executor<T>` and `[SendsMessage]` annotations.
3. The framework routes by type — no manual dispatch needed.

### Adding a new search tool

1. Create a new `Executor<SearchRequest>` that handles a new `Tool` value (e.g. `"search_graph"`).
2. Add a conditional edge in the workflow: `.AddEdge<SearchRequest>(queryRewriter, newSearch, condition: msg => msg?.Tool == "search_graph")`.
3. Update the Planner's system prompt to include the new tool in its available tools list.

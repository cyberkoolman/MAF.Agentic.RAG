# Agentic Deep-Thinking RAG Pipeline

A production-quality **Retrieval-Augmented Generation** system built on the **Microsoft Agent Framework**, **Azure AI Foundry**, and **Tavily Search**. The repository ships two contrasting RAG pipelines — a **One-Shot** pipeline and a **Deep-Thinking Agentic** pipeline — so you can compare quality, latency, and architecture side by side.

---

## Two Pipelines, Same Infrastructure

| | **One-Shot RAG** | **Deep-Thinking Agentic RAG** |
|---|---|---|
| **Topology** | Linear (single pass) | Cyclical (agent-driven loop) |
| **Planning** | None — raw query hits search directly | Multi-step tool-aware plan |
| **Query optimization** | None | Per-step rewriting tuned to the search tool |
| **Retrieval scope** | Document search (vector / keyword / hybrid) | Document search (vector / keyword / hybrid) + Web search |
| **Post-retrieval** | Reranker → LLM answer | Reranker → Distiller → Reflection → Policy |
| **Memory** | Stateless | `RagState` tracks plan, findings, and evidence |
| **Control flow** | One and done | Continue / Re-think / Finish loop |
| **Best for** | Simple factual questions | Complex multi-hop queries across sources |

See [`RAG.Comparison.md`](RAG.Comparison.md) for full ASCII architecture diagrams, a side-by-side query walkthrough, and LLM call count comparison.

---

## What Is One-Shot RAG?

One-Shot RAG is the standard, single-pass pattern: select a retrieval strategy → retrieve chunks → place them into a prompt → generate. It's fast, cheap, and works well for simple factual lookups against a single source.

The pipeline is intentionally minimal:

1. **Search** — pass the raw user query to the shared retrieval supervisor, which selects vector, keyword, or hybrid search and returns the top-K documents.
2. **Rerank** — precision-filter the candidates using an LLM judge.
3. **Answer** — pass the top results and the original question to the LLM in a single call.

No planning. No query rewriting. No reflection. One pass and done.

This works fine for straightforward questions ("What was NVIDIA's total revenue?") but breaks down for complex, multi-part questions that require connecting information across sources or combining internal documents with current web data.

---

## What Is Deep-Thinking RAG?

**Deep-Thinking RAG** treats answering as a research process, replacing the linear one-shot approach with a cyclic, agent-driven reasoning loop:

1. **Plan** — decompose the question into an ordered set of sub-questions, choosing the right tool for each step (`search_docs` for the internal knowledge base, `search_web` for live results).
2. **Rewrite** — rephrase each sub-question for optimal retrieval at that point in the research.
3. **Retrieve** — fetch from the vector store (semantic + keyword + hybrid) or the web via Tavily.
4. **Rerank** — precision-filter the top results using an LLM judge.
5. **Distil** — compress the reranked evidence into a dense, citation-preserving paragraph.
6. **Reflect** — evaluate whether the accumulated evidence is sufficient or more research is needed.
7. **Govern** — decide to continue (loop back) or finish (proceed to synthesis).
8. **Synthesise** — write a comprehensive, citable final answer from the complete research history.

This loop runs up to a configurable maximum of iterations, ensuring the model never stops too early or spins indefinitely.

---

## Architecture

### One-Shot RAG

```
User Query
    │
[Gateway] ──── (URL/source handling)
    │
[QueryBridge] ─── raw query, no rewriting, no planning
    │
[VectorSearch] ── broad recall (top-K)
    │
[Reranker] ────── precision filter (top-3)
    │
[OneShotAnswer] ─ single LLM call → final answer
```

### Deep-Thinking Agentic RAG

```
User Query
    │
[Gateway] ──── URL/file path detected → load & index source, done
    │           No source yet         → prompt for a URL, done
    │           Has source + query    ↓
[Planner]  ─── decomposes into N sub-questions
    │
[QueryRewriter] ◄──── StepSignal(n) ─── [Policy] ◄─────────────────────┐
    │                                        │ FinishSignal            │
    ├── search_docs ──► [VectorSearch]       ▼                         │
    └── search_web  ──► [WebSearch]    [Synthesis] → final answer      │
                              │                                        │
                         [Reranker]                                    │
                              │                                        │
                         [Distiller]                                   │
                              │                                        │
                         [Reflection] ─── PolicySignal ────────────────┘
```

See [`Agentic.RAG.Pipeline.md`](Agentic.RAG.Pipeline.md) for the full Mermaid block diagram.

---

## State Responsibilities

The code uses two different state classes with different lifetimes:

| State | Lifetime and scope | Purpose |
|---|---|---|
| `RagState` | One deep-thinking workflow run; stored in `IWorkflowContext` under scope `"rag"` and key `"state"` | Tracks the user query, research plan, current step, findings, distilled evidence, and iteration count. The one-shot pipeline does not use it. |
| `KnowledgeBaseState` | Shared application state across workflow runs and both DevUI agents | Tracks whether a source has been loaded and provides a source description to the gateway and planner. It does not contain document chunks or research history. |
| `VectorStore` | Shared application service | Holds the actual `RagDocument` chunks and embeddings used by internal retrieval. |

`KnowledgeBaseState` describes the available knowledge base; `RagState` is the agentic workflow's per-question research notebook.

---

## Projects

| Project | Description |
|---|---|
| `AgenticRAG` | Core library — executors, workflow graph, services, models |
| `AgenticRAG.Api` | ASP.NET Core host with DevUI chat window |

---

## Executor Nodes

### One-Shot Pipeline

| Node | Role |
|---|---|
| **Gateway** | Root node (reused) — handles URL loading and source checks |
| **QueryBridge** | Passes the raw query directly to vector search — no LLM call, no rewriting |
| **VectorSearch** | Semantic + keyword + hybrid search against the in-memory vector store (reused) |
| **Reranker** | LLM-based precision filtering — keeps the top-K most relevant chunks (reused) |
| **OneShotAnswer** | Single LLM call: raw reranked docs + query → answer. No distillation, no reflection |

### Deep-Thinking Pipeline

| Node | Role |
|---|---|
| **Gateway** | Root node. Routes incoming messages: loads sources, rejects premature queries, or passes through to the Planner |
| **Planner** | Decomposes the user question into a research plan with tool assignments |
| **QueryRewriter** | Rewrites each sub-question for precise retrieval |
| **VectorSearch** | Semantic + keyword + hybrid search against the in-memory vector store |
| **WebSearch** | Live web search via Tavily |
| **Reranker** | LLM-based precision filtering — keeps the top-K most relevant chunks |
| **Distiller** | Compresses evidence into a dense, citation-rich paragraph |
| **Reflection** | Evaluates research quality and updates the shared state |
| **Policy** | Decides: continue researching (→ QueryRewriter) or finish (→ Synthesis) |
| **Synthesis** | Writes the final, multi-hop answer with inline clickable citations |

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- **Azure AI Foundry** resource with deployments for:
  - A reasoning model (e.g. `gpt-4o`)
  - A fast model (e.g. `gpt-4o-mini`)
  - An embedding model (e.g. `text-embedding-3-large`)
- **Tavily** API key — [tavily.com](https://tavily.com)

---

## Configuration

Copy `appsettings.json` and create `appsettings.Local.json` (git-ignored) with your credentials:

```json
{
  "AzureAI": {
    "Endpoint": "https://YOUR_RESOURCE.cognitiveservices.azure.com/",
    "ApiKey": "YOUR_KEY",
    "ReasoningModel": "gpt-4o",
    "FastModel": "gpt-4o-mini",
    "EmbeddingModel": "text-embedding-3-large"
  },
  "Tavily": {
    "ApiKey": "YOUR_TAVILY_KEY"
  },
  "Pipeline": {
    "ChunkSize": 500,
    "ChunkOverlap": 50,
    "InitialRetrievalTopK": 10,
    "RerankerTopK": 3,
    "MaxIterations": 10
  }
}
```

---

## Running the DevUI

```bash
cd AgenticRAG.Api
dotnet run
```

Open **http://localhost:8888/devui** in your browser.

1. Select an agent from the dropdown:
   - **OneShot-RAG** — single-pass linear pipeline (fast, simple)
   - **Agentic-RAG** — deep-thinking multi-hop pipeline with planning, reflection, and iterative retrieval
2. Paste a URL or file path to load a knowledge source — the pipeline will chunk and embed it. Documents are shared across both agents.
3. Ask any question. Try the same query in both agents to compare answer quality and depth.

Multiple sources can be added at any time and accumulate in the shared knowledge base.

---

## Running the Console Pipeline

```bash
cd AgenticRAG
dotnet run
```

At startup you'll be prompted to select a pipeline mode:

```
Select pipeline:
  [1] One-Shot RAG       (single-pass, linear)
  [2] Deep-Thinking RAG  (multi-hop, agentic loop)
```

Configure sources in `appsettings.Local.json` under `Knowledge.Sources`, or enter a query interactively at the prompt. Set `Knowledge.DefaultQuery` to skip the interactive prompt.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Agent orchestration | [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework) |
| LLM & embeddings | Azure AI Foundry (OpenAI models) |
| Web search | [Tavily](https://tavily.com) |
| Vector search | In-memory (cosine similarity + BM25-style hybrid) |
| Chat UI | Microsoft.Agents.AI.DevUI |
| Runtime | .NET 9, ASP.NET Core |

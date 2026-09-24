# RAG Pipeline Comparison — Deep-Thinking vs One-Shot

## At a Glance

| | **One-Shot RAG** | **Deep-Thinking Agentic RAG** |
|---|---|---|
| **Topology** | Linear (single pass) | Cyclical (agent-driven loop) |
| **Planning** | None — raw query hits search directly | Multi-step tool-aware plan |
| **Query optimization** | None | Per-step rewriting tuned to the search tool |
| **Retrieval scope** | Document search (vector / keyword / hybrid) | Document search (vector / keyword / hybrid) + Web search |
| **Post-retrieval** | Reranker → LLM answer | Reranker → Distiller → Reflection → Policy |
| **Memory across steps** | None (stateless) | `RagState` tracks plan, findings, and evidence |
| **Control flow** | One and done | Continue / Re-think / Finish loop |
| **Best for** | Simple factual questions over a single source | Complex multi-hop queries spanning multiple sources |
| **Weakness** | Falls apart on multi-hop reasoning | Higher latency and token cost |

---

## One-Shot RAG — Architecture

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         ONE-SHOT RAG PIPELINE                           │
│                                                                         │
│   ┌──────────┐    ┌───────────────┐    ┌──────────┐    ┌────────────┐   │
│   │          │    │               │    │          │    │            │   │
│   │ Gateway  ├───►│ Vector Search ├───►│ Reranker ├───►│   Answer   │   │
│   │          │    │               │    │          │    │            │   │
│   └──────────┘    └───────────────┘    └──────────┘    └────────────┘   │
│                                                                         │
│   raw query        top-K docs          top-3 ranked    single LLM call  │
│   (no rewrite)     (broad recall)      (precision)     (yield output)   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Service Diagram

```
┌────────────────────────┐
│        User Query      │
└───────────┬────────────┘
            │
            ▼
┌───────────────────────┐
│     GatewayExecutor   │  Handles URL loading & no-source checks
│  (reused from Agentic)│  Sends UserQuery downstream
└───────────┬───────────┘
            │ UserQuery
            ▼
┌───────────────────────┐
│   QueryBridgeExecutor │  Maps raw query → SearchRequest
│   (nested in workflow)│  tool = "search_docs", no LLM call
└───────────┬───────────┘
            │ SearchRequest
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│  VectorSearchExecutor │────►│  Azure AI Foundry   │
│  (reused from Agentic)│     │  (Embedding Model)  │
│                       │     └─────────────────────┘
│  • Strategy Supervisor│     ┌─────────────────────┐
│    (vector/keyword/   │────►│  Azure AI Foundry   │
│     hybrid selection) │     │  (Fast Model)       │
│  • In-memory index    │     └─────────────────────┘
└───────────┬───────────┘
            │ SearchResults (top-K)
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│   RerankerExecutor    │────►│  Azure AI Foundry   │
│  (reused from Agentic)│     │  (Fast Model)       │
│                       │     └─────────────────────┘
│  Cross-encoder proxy: │
│  scores & filters to  │
│  top-3 documents      │
└───────────┬───────────┘
            │ RankedResults (top-3)
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│ OneShotAnswerExecutor │────►│  Azure AI Foundry   │
│  (new, terminal node) │     │  (Reasoning Model)  │
│                       │     └─────────────────────┘
│  Single LLM call:     │
│  docs + query → answer│
│  No distillation,     │
│  no reflection, no    │
│  memory of past steps │
└───────────┬───────────┘
            │ yield final output
            ▼
┌────────────────────────┐
│     Final Answer       │
└────────────────────────┘
```

### What One-Shot RAG Skips

| Agentic Stage | What's Lost |
|---|---|
| **Planner** | No decomposition — the raw query is the only search attempt |
| **Query Rewriter** | No optimization — vague queries produce vague retrieval |
| **Web Search** | No external data — can't answer questions beyond the indexed docs |
| **Distiller** | No noise reduction — all top-3 chunks go raw into the LLM |
| **Reflection** | No memory — can't build on previous findings |
| **Policy Agent** | No control flow — can't loop back or revise the approach |
| **Synthesis** | No multi-step evidence integration — one shot is all you get |

---

## Deep-Thinking Agentic RAG — Architecture

### Component Diagram

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│                         DEEP-THINKING AGENTIC RAG PIPELINE                         │
│                                                                                    │
│   ┌─────────┐   ┌─────────┐   ┌──────────┐   ┌──────────────┐   ┌──────────────┐   │
│   │         │   │         │   │          │   │              │   │              │   │
│   │ Gateway ├──►│ Planner ├──►│ Rewriter ├──►│ Vector / Web ├──►│   Reranker   │   │
│   │         │   │         │   │          │   │    Search    │   │              │   │
│   └─────────┘   └─────────┘   └──────────┘   └──────────────┘   └──────┬───────┘   │
│                                     ▲                                  │           │
│                                     │                                  ▼           │
│                               ┌─────┴─────┐   ┌────────────┐   ┌──────────────┐    │
│                               │           │   │            │   │              │    │
│                               │  Policy   │◄──┤ Reflection │◄──┤  Distiller   │    │
│                               │           │   │            │   │              │    │
│                               └─────┬─────┘   └────────────┘   └──────────────┘    │
│                                     │                                              │
│                          ┌──────────┴──────────┐                                   │
│                          │ FINISH              │ CONTINUE                          │
│                          ▼                     │ (loops back to Rewriter)          │
│                   ┌─────────────┐              │                                   │
│                   │  Synthesis  │              │                                   │
│                   └─────────────┘              │                                   │
│                                                                                    │
└────────────────────────────────────────────────────────────────────────────────────┘
```

### Service Diagram

```
┌────────────────────────┐
│      User Query        │
└───────────┬────────────┘
            │
            ▼
┌───────────────────────┐
│     GatewayExecutor   │  URL loading & source checks
└───────────┬───────────┘
            │ UserQuery
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│    PlannerExecutor    │────►│  Azure AI Foundry   │
│                       │     │  (Reasoning Model)  │
│  Decomposes query     │     └─────────────────────┘
│  into multi-step plan │
│  with tool assignment │
│  Writes → RagState    │
└───────────┬───────────┘
            │ StepSignal(0)
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│ QueryRewriterExecutor │────►│  Azure AI Foundry   │
│                       │     │  (Fast Model)       │
│  Optimizes sub-query  │     └─────────────────────┘
│  for the target tool  │
└─────────┬─────────────┘
          │ SearchRequest
    ┌─────┴──────┐
    ▼            ▼
┌────────┐  ┌────────────┐     ┌─────────────────────┐
│ Vector │  │    Web      │────►│  Tavily Search API │
│ Search │  │   Search    │     └────────────────────┘
└───┬────┘  └─────┬──────┘
    └──────┬──────┘
           │ SearchResults
           ▼
┌───────────────────────┐     ┌─────────────────────┐
│   RerankerExecutor    │────►│  Azure AI Foundry   │
│   (Cross Encoder)     │     │  (Fast Model)       │
└───────────┬───────────┘     └─────────────────────┘
            │ RankedResults
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│   DistillerExecutor   │────►│  Azure AI Foundry   │
│   Compresses top-3    │     │  (Fast Model)       │
│   into dense paragraph│     └─────────────────────┘
└───────────┬───────────┘
            │ DistilledContext
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│  ReflectionExecutor   │────►│  Azure AI Foundry   │
│  Updates RagState     │     │  (Fast Model)       │
│  research history     │     └─────────────────────┘
└───────────┬───────────┘
            │ PolicySignal
            ▼
┌───────────────────────┐     ┌─────────────────────┐
│    PolicyExecutor     │────►│  Azure AI Foundry   │
│                       │     │  (Reasoning Model)  │
│  CONTINUE → loop back │     └─────────────────────┘
│  FINISH   → synthesize│
└─────────┬─────────────┘
          │ FinishSignal
          ▼
┌───────────────────────┐     ┌─────────────────────┐
│  SynthesisExecutor    │────►│  Azure AI Foundry   │
│  Multi-hop evidence   │     │  (Reasoning Model)  │
│  integration, cites   │     └─────────────────────┘
│  all sources          │
└───────────┬───────────┘
            │ yield final output
            ▼
┌────────────────────────┐
│     Final Answer       │
└────────────────────────┘
```

---

## Side-by-Side: What Happens with a Complex Query

**Example query:** *"How do NVIDIA's data center revenue growth and supply chain risks from its 10-K compare with what analysts are saying about competition from AMD and custom silicon?"*

| Step | One-Shot RAG | Deep-Thinking Agentic RAG |
|---|---|---|
| **1. Query handling** | Sends the entire 30-word question as-is to vector search | Planner decomposes into 3 sub-questions: revenue data, supply chain risks, competitive landscape |
| **2. Tool selection** | Always `search_docs` | Step 1–2: `search_docs` (10-K data), Step 3: `search_web` (analyst opinions) |
| **3. Query → search** | Raw question embedded directly | Each sub-question rewritten for its target tool (e.g., "NVIDIA data center segment revenue FY2024" for docs) |
| **4. Retrieval** | 10 chunks → reranked to 3 | 10 chunks per step, across 3 steps = 30 chunks evaluated total |
| **5. Context prep** | 3 raw chunks concatenated | Each step's top-3 distilled into a dense paragraph; noise removed |
| **6. Reflection** | None | After each step: "What did we learn? What's still missing?" |
| **7. Answer** | Single LLM call with 3 chunks | Synthesis agent integrates all distilled evidence with multi-hop reasoning |
| **Typical result** | Partially answers one aspect; misses the cross-source comparison entirely | Comprehensive answer covering all three aspects with inline citations |

---

## Why Results Diverge — Even on Seemingly Simple Queries

A query like *"What are NVIDIA's top risk factors?"* looks like a One-Shot candidate — single source, single section. In practice, it often produces noticeably different results between the two pipelines. Here's why:

### 1. Chunking splits the risk list

A 10-K "Risk Factors" section can run 15–20 pages. After chunking at ~500 tokens, those risks are spread across 10–30 chunks. One-Shot embeds the raw question and retrieves the 10 chunks most semantically similar to the phrase *"top risk factors"* — which are often the **header chunk** and the first few named risks. The rest are never seen.

The agentic pipeline decomposes the query into sub-questions like *"supply chain risks"*, *"competitive risks"*, and *"regulatory risks"* — each sub-question retrieves a different slice of the risk section.

### 2. Query embedding vs. content embedding mismatch

The phrase "top risk factors" is conceptually generic. Document chunks contain specific language: *"geopolitical export restrictions"*, *"customer concentration"*, *"semiconductor manufacturing lead times"*. The vector distance between the generic question embedding and specific chunk embeddings is often poor — the reranker can only work with what retrieval returns.

The Query Rewriter solves this by generating targeted queries closer to the vocabulary actually used in the document.

### 3. No coverage awareness in One-Shot

One-Shot has no way to know that 3 retrieved chunks cover the same risk (supply chain) from three angles while 5 other risk categories were never retrieved. The Reflection + Policy loop catches this: the research history makes the gap explicit, and the Policy agent can continue to fill it.

### Summary

| Root Cause | One-Shot behavior | Agentic behavior |
|---|---|---|
| Chunking splits content across many chunks | Retrieves top-10 by proximity to the generic question | Each sub-question targets a specific slice |
| Generic query ≠ specific document vocabulary | Embedding mismatch lowers recall | Query Rewriter aligns vocabulary to the document |
| No coverage tracking | Cannot detect what was missed | Reflection logs findings; Policy detects gaps |

---

## When to Use Which

| Scenario | Recommended Pipeline |
|---|---|
| "What is NVIDIA's total revenue?" | ✅ One-Shot — single fact, single source |
| "Summarize the risk factors" | ✅ One-Shot — single section, no cross-referencing needed |
| "Compare revenue trends across segments and explain supply chain exposure" | ✅ Deep-Thinking — multi-hop, needs planning |
| "How does the 10-K filing compare with recent analyst sentiment?" | ✅ Deep-Thinking — requires docs + web search |
| Quick demo or latency-sensitive use case | ✅ One-Shot — fast, cheap, predictable |
| Production research or due-diligence workflow | ✅ Deep-Thinking — thorough, verifiable |

---

## LLM Call Count Comparison

```
ONE-SHOT RAG                    DEEP-THINKING RAG (3-step plan)
─────────────                   ─────────────────────────────────
1× Strategy Supervisor (fast)   1× Planner (reasoning)
1× Reranker (fast)              3× Query Rewriter (fast)
1× Answer (reasoning)           3× Strategy Supervisor (fast)
                                3× Reranker (fast)
Total: 3 LLM calls              3× Distiller (fast)
                                3× Reflection (fast)
                                3× Policy (reasoning)
                                1× Synthesis (reasoning)
                                ─────────────────────────────────
                                Total: ~20 LLM calls
```

The cost of the agentic pipeline is the price of **depth** — and for complex queries, that depth is the difference between a partial answer and a correct one.

---

## Shared Infrastructure

Both pipelines share the same underlying services — no duplication:

```
┌─────────────────────────────────────────────────────────┐
│                   SHARED SERVICES                       │
│                                                         │
│   ┌─────────────────┐   ┌────────────────────────────┐  │
│   │  AzureAIService │   │  VectorStore               │  │
│   │  • Reasoning LLM│   │  • In-memory index         │  │
│   │  • Fast LLM     │   │  • Vector / keyword /      │  │
│   │  • Embeddings   │   │    hybrid search           │  │
│   └─────────────────┘   └────────────────────────────┘  │
│                                                         │
│   ┌─────────────────┐   ┌────────────────────────────┐  │
│   │  TavilyService  │   │  DocumentLoader            │  │
│   │  (web search)   │   │  • URL / file ingestion    │  │
│   │                 │   │  • Chunking & embedding    │  │
│   └─────────────────┘   └────────────────────────────┘  │
│                                                         │
│   ┌─────────────────────────────────────────────────┐   │
│   │  KnowledgeBaseState (shared application state)  │   │
│   │  • Tracks loaded sources across both pipelines  │   │
│   └─────────────────────────────────────────────────┘   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

`KnowledgeBaseState` only tracks whether sources are loaded and describes them to the gateway and planner. The actual indexed chunks live in `VectorStore`. The deep-thinking pipeline creates a separate `RagState` inside `IWorkflowContext` for each question; it stores the plan, findings, distilled evidence, and loop progress for that workflow run.

---

### Key Takeaway

> **Architecture over intelligence.** Both pipelines use the same LLMs, the same embeddings, the same vector store. The difference in answer quality comes entirely from *how the pipeline is wired* — planning, reflection, and control flow are what turn a language model into a research agent.

---

*Companion to: [Agentic.RAG.Pipeline.md](Agentic.RAG.Pipeline.md) — full block diagram and stage-by-stage description of the deep-thinking pipeline.*

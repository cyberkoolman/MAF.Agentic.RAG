# Agentic Deep-Thinking RAG Pipeline — Mermaid Block Diagram

> This file contains the **Mermaid renderable block diagram** for the deep-thinking pipeline.
> For architecture descriptions, executor details, and One-Shot vs Deep-Thinking comparison, see [`README.md`](README.md) and [`RAG.Comparison.md`](RAG.Comparison.md).

## Block Diagram

```mermaid
flowchart LR

  %% ── Input Layer ──────────────────────────────────────────────
  subgraph INPUT["📥 Input"]
    DATA["Unstructured &\nStructured Data"]
    QUERY["Multi-Source\nMulti-Hop Query"]
  end

  %% ── Pre-processing ───────────────────────────────────────────
  subgraph PREPROC["🔧 Pre-processing"]
    CLEAN["Clean & Reduce\nTokens"]
  end

  %% ── Strategic Planning ───────────────────────────────────────
  subgraph PLANNING["🧠 Strategic Planning / Query Formulation"]
    PLANNER["Tool-Aware\nPlanner"]
    REWRITER["Query\nRewriter Agent"]
    META["Metadata-Aware\nChunking"]
    PLANNER --> REWRITER
    PLANNER --> META
  end

  %% ── Vector Strategy ──────────────────────────────────────────
  subgraph VECTOR["🔍 Vector Strategy Choosing Agent"]
    HYBRID["Hybrid\nSearch"]
    KEYWORD["Keyword\nSearch"]
    SEMANTIC["Semantic\nSearch"]
  end

  %% ── Retrieval Funnel ─────────────────────────────────────────
  subgraph RETRIEVAL["📚 Retrieval Funnel"]
    SUPER["Supervisor\nAgent"]
    CROSS["Cross\nEncoder"]
    DISTILL["Contextual\nDistillation"]
    WEBSEARCH["Web\nSearch"]
    SUPER --> CROSS
    SUPER --> WEBSEARCH
    CROSS --> DISTILL
  end

  %% ── Reflection ───────────────────────────────────────────────
  subgraph REFLECT["🪞 Update & Reflect"]
    CUMULATIVE["Cumulative\nResearch Memory"]
  end

  %% ── Self-Critique & Control Flow ─────────────────────────────
  subgraph CRITIQUE["⚙️ Self-Critique & Control Flow Policy"]
    POLICY["Policy\nAgent"]
    POLICY -->|"Continue"| PLANNING
    POLICY -->|"Re-thinking"| PLANNING
  end

  %% ── Output ───────────────────────────────────────────────────
  subgraph OUTPUT["💡 Output"]
    RESPONSE["Final\nResponse"]
  end

  %% ── Evaluation ───────────────────────────────────────────────
  subgraph EVAL["📊 Evaluation of Reasoning Engine"]
    QUALITATIVE["Qualitative\n(LLM Judge:\nFaithfulness, Relevance,\nSoundness, Depth)"]
    QUANTITATIVE["Quantitative\n(Precision &\nRecall)"]
    PERFORMANCE["Performance\n(Latency &\nToken Cost)"]
  end

  %% ── Main Flow ────────────────────────────────────────────────
  DATA --> PREPROC
  QUERY --> PLANNING
  PREPROC --> PLANNING

  PLANNING --> VECTOR
  VECTOR --> RETRIEVAL

  RETRIEVAL --> REFLECT
  REFLECT --> CRITIQUE
  CRITIQUE -->|"Finish"| OUTPUT
  OUTPUT --> EVAL
```

## Phase-by-Phase Diagram

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                     DEEP-THINKING AGENTIC RAG — 6-PHASE LOOP                        │
│                     "How a good researcher actually works"                          │
│                                                                                     │
│                            User Query                                               │
│                                │                                                    │
│                                ▼                                                    │
│   ┌─────────────────────────────────────────────────────────────────────────────┐   │
│   │  Phase 1 — PLAN                                                             │   │
│   │  "Figure out what to find before you start looking"                         │   │
│   │                                                                             │   │
│   │  Planner breaks the question into N sub-questions.                          │   │
│   │  Each gets a tool:   search_docs  (internal)   or   search_web  (live)      │   │
│   └────────────────────────────┬────────────────────────────────────────────────┘   │
│                                │ StepSignal(0)                                      │
│   ┌────────────────────────────▼────────────────────────────────────────────────┐   │
│   │  ↻  RESEARCH LOOP  ──────────────────────────────────────────────────────┐  │   │
│   │  │                                                                        │  │  │
│   │  │  Phase 2 — RETRIEVE   "Cast a wide net"                                │  │  │
│   │  │                                                                        │  │  │
│   │  │     ┌──────────────────────────┐   ┌──────────────────────────┐        │  │  │
│   │  │     │      search_docs         │   │       search_web         │        │  │  │
│   │  │     │  Vector / Keyword /      │   │  Tavily · live results   │        │  │  │
│   │  │     │  Hybrid  ·  Top 10       │   │  Top 5 web pages         │        │  │  │
│   │  │     └────────────┬─────────────┘   └─────────────┬────────────┘        │  │  │
│   │  │                  └──────────────┬─────────────────┘                    │  │  │
│   │  │                          Top 10 candidates                             │  │  │
│   │  │                                 │                                      │  │  │
│   │  │  Phase 3 — REFINE   "Right words in · only signal out"                 │  │  │
│   │  │                                                                        │  │  │
│   │  │     Query Rewriter  ─────────►  sharpen query before search            │  │  │
│   │  │     Distiller  ──────────────►  top 3 chunks → 1 dense paragraph       │  │  │
│   │  │                                                                        │  │  │
│   │  │  Phase 4 — REFLECT   "Write it in the notebook"                        │  │  │
│   │  │                                                                        │  │  │
│   │  │     Reflection Agent  ───────►  1 factual sentence                     │  │  │
│   │  │     RagState.ResearchHistory  ►  [ step 0,  step 1,  step 2 … ]        │  │  │
│   │  │                                                                        │  │  │
│   │  │  Phase 5 — CRITIQUE   "Do I know enough yet?"                          │  │  │
│   │  │                                                                        │  │  │
│   │  │     Policy Agent reads full RagState                                   │  │  │
│   │  │                                                                        │  │  │
│   │  │          CONTINUE                              FINISH                  │  │  │
│   │  │    (next sub-question)                   (evidence complete)           │  │  │
│   │  │          │                                      │                      │  │  │
│   │  └──────────┘                                      │                      ┘  │  │
│   │  ▲──────── loops back to Phase 2 with next step ───┘                         │  │
│   └──────────────────────────────────────────────────────────────────────────────┘  │
│                                          │ FINISH                                   │
│                                          ▼                                          │
│   ┌──────────────────────────────────────────────────────────────────────────────┐  │
│   │  Phase 6 — SYNTHESIZE   "Sit down and write the final answer"                │  │
│   │                                                                              │  │
│   │  Synthesis Agent reads full RagState  (all steps · all evidence)             │  │
│   │  Writes cited, multi-source, comprehensive answer                            │  │
│   └────────────────────────────────┬─────────────────────────────────────────────┘  │
│                                    │                                                │
│                              Final Answer ✓                                         │
│                                                                                     │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

---

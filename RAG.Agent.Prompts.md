# Prompt Steps

This document inventories every LLM prompt used by the Agentic RAG workflows. The
steps appear in execution order. Runtime values are shown as `{{placeholders}}`.

## Shared Prompt Behavior

Every LLM call sends exactly two chat messages:

1. A system message containing the stage's system prompt.
2. A user message containing the runtime prompt.

Calls made through `CompleteStructuredAsync<T>` append this text to the system
prompt before sending it:

```text
IMPORTANT: Respond with valid JSON only. No markdown fences, no explanations — just the JSON object.
```

**Source:** [`AzureAIService.cs` lines 55–98](AgenticRAG/Services/AzureAIService.cs#L55-L98)

The deep-thinking workflow uses the reasoning model for Planner, Policy, and
Synthesis. It uses the fast model for Query Rewriter, Retrieval Supervisor,
Reranker, Distiller, and Reflection.

---

## Deep-Thinking Pipeline

### 1. Planner

- **Executor:** `PlannerExecutor`
- **Model tier:** Reasoning
- **Structured output:** Yes
- **Source:** [`PlannerExecutor.cs` lines 43–74](AgenticRAG/Executors/PlannerExecutor.cs#L43-L74)

#### System prompt

```text
You are a strategic research planning agent.

Available tools:
  • search_docs — searches the internal knowledge base which contains:
    {{knowledgeBaseDescription}}
  • search_web  — performs a live web search (use for current events,
    recent news, or anything not covered by the internal documents)

Task: Decompose the user's query into a minimal, ordered set of sub-questions.
For each step choose the correct tool based on where the answer is likely to be found.

Rules:
- Use search_docs first when the answer should exist in the knowledge base.
- Use search_web when the answer requires live or post-publication information.
- Keep the plan to 2–5 steps; avoid redundant steps.
- Sequence steps so earlier findings can enrich later queries.

Return JSON exactly:
{
  "steps": [
    { "subQuestion": "...", "reasoning": "...", "tool": "search_docs" },
    { "subQuestion": "...", "reasoning": "...", "tool": "search_web"  }
  ]
}
```

#### User prompt

```text
Create a research plan for:

{{userQuery}}
```

#### Output

```json
{
  "steps": [
    {
      "subQuestion": "...",
      "reasoning": "...",
      "tool": "search_docs"
    }
  ]
}
```

If the model produces no steps, the executor creates one `search_docs` step
using the original query. The Planner then sends `StepSignal(0)`.

---

### 2. Query Rewriter

- **Executor:** `QueryRewriterExecutor`
- **Model tier:** Fast
- **Structured output:** No
- **Source:** [`QueryRewriterExecutor.cs` lines 64–103](AgenticRAG/Executors/QueryRewriterExecutor.cs#L64-L103)

The first `search_docs` step does not invoke the LLM when research history is
empty. It passes the original user query through unchanged. Later document
steps and all web-search steps use the following prompts.

#### System prompt

```text
You are a search query optimisation specialist.

Rewrite the given sub-question into the single most effective search query for
the specified tool:
  • search_docs → incorporate key findings from previous research steps to focus
                  the query on what is still unknown. Keep natural language phrasing
                  for conceptual questions; use precise keyword form only for exact
                  section titles, product model numbers, or financial figures.
  • search_web  → use specific, current terms that will surface recent news,
                  analyst reports, or press releases.

Return ONLY the rewritten query string — no explanation, no punctuation wrappers.
```

#### User prompt

With no previous findings:

```text
Sub-question : {{subQuestion}}
Tool         : {{tool}}

Rewritten query:
```

With previous findings:

```text
Sub-question : {{subQuestion}}
Tool         : {{tool}}

Previous findings:
{{researchHistory}}

Rewritten query:
```

#### Output

One plain search-query string. The executor trims whitespace and surrounding
single or double quotation marks.

---

### 3A. Retrieval Supervisor

- **Executor:** `VectorSearchExecutor`
- **Model tier:** Fast
- **Structured output:** No
- **Source:** [`VectorSearchExecutor.cs` lines 70–100](AgenticRAG/Executors/VectorSearchExecutor.cs#L70-L100)

This prompt runs only when the Planner selected `search_docs`. It chooses the
retrieval algorithm before the internal knowledge base is searched.

#### System prompt

```text
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
```

#### User prompt

```text
Query: {{rewrittenQuery}}
```

#### Output

```text
vector
```

The other accepted outputs are `keyword` and `hybrid`. Any unrecognized output
defaults to `hybrid`.

---

### 3B. Web Search

- **Executor:** `WebSearchExecutor`
- **LLM prompt:** None
- **Source:** [`WebSearchExecutor.cs`](AgenticRAG/Executors/WebSearchExecutor.cs)

When the Planner selects `search_web`, the rewritten query is sent to Tavily.
The returned web results are converted into `RagDocument` records and passed to
the same Reranker used by document search.

---

### 4. Reranker

- **Executor:** `RerankerExecutor`
- **Model tier:** Fast
- **Structured output:** Yes
- **Source:** [`RerankerExecutor.cs` lines 44–82](AgenticRAG/Executors/RerankerExecutor.cs#L44-L82)

Each candidate is numbered and represented by its source, section, and up to
400 characters of content.

#### System prompt

```text
You are a precision relevance reranker.

Given a question and numbered document excerpts, return the indices of the
most relevant documents in descending relevance order.

Return JSON exactly:
{ "ranked_indices": [2, 0, 5] }

Include only documents that genuinely help answer the question.
```

#### User prompt

```text
Question: {{subQuestion}}

Documents:
[0] Source: {{source0}} | Section: {{section0}}
{{document0Preview}}

[1] Source: {{source1}} | Section: {{section1}}
{{document1Preview}}

Return the {{rerankerTopK}} most relevant indices:
```

#### Output

```json
{ "ranked_indices": [2, 0, 5] }
```

Invalid and duplicate indices are removed. The executor keeps at most the
configured top-K documents. If structured parsing fails, it uses the first
top-K candidates in their original order.

---

### 5. Distiller

- **Executor:** `DistillerExecutor`
- **Model tier:** Fast
- **Structured output:** No
- **Source:** [`DistillerExecutor.cs` lines 32–72](AgenticRAG/Executors/DistillerExecutor.cs#L32-L72)

The Distiller receives the complete content and provenance metadata for each
reranked document.

#### System prompt

```text
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
```

#### User prompt

```text
Question: {{subQuestion}}

Documents:
Source : {{source0}}
Section: {{section0}}
URL    : {{url0}}

{{document0Content}}

---

Source : {{source1}}
Section: {{section1}}
URL    : {{url1}}

{{document1Content}}

Distilled context:
```

#### Output

One dense, citation-preserving paragraph. It is wrapped in a
`DistilledContext` message with the sub-question and step index. If no documents
were reranked, no LLM call occurs and the context is set to:

```text
No relevant information found.
```

---

### 6. Reflection

- **Executor:** `ReflectionExecutor`
- **Model tier:** Fast
- **Structured output:** No
- **Source:** [`ReflectionExecutor.cs` lines 35–69](AgenticRAG/Executors/ReflectionExecutor.cs#L35-L69)

#### System prompt

```text
You are a research reflection agent.

Summarise the key finding from the retrieved context in exactly one concise sentence.
Be specific: include concrete facts, numbers, or named entities where present.
The sentence will be added to a research log read by a policy agent.
```

#### User prompt

```text
Original query  : {{userQuery}}
Sub-question    : {{subQuestion}}
Retrieved context:
{{distilledContext}}
```

#### Output

One factual sentence. The executor stores it in `RagState.ResearchHistory`:

```text
Step {{stepNumber}} [{{tool}}]: {{summary}}.
```

It also stores the complete distilled context in
`RagState.DistilledContexts` for final synthesis.

---

### 7. Policy

- **Executor:** `PolicyExecutor`
- **Model tier:** Reasoning
- **Structured output:** Yes
- **Source:** [`PolicyExecutor.cs` lines 42–107](AgenticRAG/Executors/PolicyExecutor.cs#L42-L107)

The Policy prompt runs only when another planned step remains and the iteration
limit has not been reached. Otherwise, the executor finishes without an LLM
call.

#### System prompt

```text
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
```

#### User prompt

```text
Original query:
{{userQuery}}

Plan:
  1. [{{tool}}] {{status}}  {{subQuestion}}
  2. [{{tool}}] {{status}}  {{subQuestion}}

Research history:
{{researchHistory}}
```

The runtime status is `✓ DONE` or `○ PENDING`.

#### Output

```json
{ "action": "CONTINUE" }
```

The other accepted decision is `FINISH`. A missing, malformed, or unrecognized
action defaults to `CONTINUE`. `CONTINUE` sends the next step back to Query
Rewriter; `FINISH` sends a `FinishSignal` to Synthesis.

---

### 8. Synthesis

- **Executor:** `SynthesisExecutor`
- **Model tier:** Reasoning
- **Structured output:** No
- **Source:** [`SynthesisExecutor.cs` lines 39–74](AgenticRAG/Executors/SynthesisExecutor.cs#L39-L74)

#### System prompt

```text
You are a senior research synthesis agent.

Your task: write a comprehensive, well-structured answer to the original query
by integrating all research findings.

Guidelines:
1. Perform multi-hop reasoning — explicitly connect facts across sources.
2. Use specific details: cite sources inline as [Source Name / Section](URL) when a URL is present in the evidence, preserving markdown links exactly as provided.
3. Structure the answer with clear headings if complexity warrants it.
4. Be precise: include numbers, dates, product names where available.
5. Address every aspect of the original query.
6. End with a concise synthesis paragraph that connects all findings.
7. Acknowledge where information is limited or uncertain.
```

#### User prompt

```text
Original query:
{{userQuery}}

Research summary:
{{researchHistory}}

Detailed evidence:
{{distilledContexts}}

Write the comprehensive answer:
```

#### Output

The final answer returned by the workflow. Synthesis receives all one-sentence
research summaries and the full distilled evidence collected across completed
steps.

---

## One-Shot Pipeline

The one-shot pipeline reuses the Retrieval Supervisor and Reranker prompts
documented above. It has no Planner, Query Rewriter, Distiller, Reflection,
Policy, or multi-step Synthesis prompt.

**Workflow source:** [`OneShotRagWorkflow.cs`](AgenticRAG/Workflow/OneShotRagWorkflow.cs)

### One-Shot Answer

- **Executor:** `OneShotAnswerExecutor`
- **Model tier:** Reasoning
- **Structured output:** No
- **Source:** [`OneShotAnswerExecutor.cs` lines 28–61](AgenticRAG/Executors/OneShotAnswerExecutor.cs#L28-L61)

#### System prompt

```text
You are a helpful research assistant.

Answer the user's question using ONLY the provided documents.
- Cite sources inline using [Source Name](URL) when a URL is available.
- Be precise: include specific numbers, dates, and names when present.
- If the documents do not contain enough information, say so clearly.
- Do NOT speculate or add information beyond what the documents provide.
```

#### User prompt

```text
Question: {{userQuery}}

Documents:
Source : {{source0}}
Section: {{section0}}
URL    : {{url0}}

{{document0Content}}

---

Source : {{source1}}
Section: {{section1}}
URL    : {{url1}}

{{document1Content}}

Answer:
```

#### Output

The final one-shot answer. If reranking returns no documents, no LLM call occurs
and the workflow returns:

```text
I could not find relevant information to answer your question.
```

---

## Prompt-Free Components

| Component | Behavior | Source |
|---|---|---|
| Gateway | Loads sources, checks knowledge-base state, and forwards user queries without an LLM prompt. | [`GatewayExecutor.cs`](AgenticRAG/Executors/GatewayExecutor.cs) |
| Query Bridge | Passes the original query directly to document search in the one-shot pipeline. | [`OneShotRagWorkflow.cs` lines 77–103](AgenticRAG/Workflow/OneShotRagWorkflow.cs#L77-L103) |
| Embedding generation | Sends text to the configured embedding model; it does not use a chat prompt. | [`AzureAIService.cs`](AgenticRAG/Services/AzureAIService.cs) |
| Vector, keyword, and hybrid search | Execute retrieval after the Retrieval Supervisor chooses a strategy. | [`VectorSearchExecutor.cs` lines 37–64](AgenticRAG/Executors/VectorSearchExecutor.cs#L37-L64) |
| Web Search | Sends the rewritten query to Tavily without a chat prompt. | [`WebSearchExecutor.cs`](AgenticRAG/Executors/WebSearchExecutor.cs) |
| Workflow routing | Routes typed messages and applies conditional edges without an LLM prompt. | [`AgenticRagWorkflow.cs` lines 66–108](AgenticRAG/Workflow/AgenticRagWorkflow.cs#L66-L108) |

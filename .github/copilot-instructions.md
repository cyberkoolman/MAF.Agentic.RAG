# Agentic RAG — Copilot Workspace Instructions

## Project Overview

This repository contains **presentation and architecture documentation** for the *Agentic Deep-Thinking RAG Pipeline* — a cyclical, agent-driven Retrieval-Augmented Generation architecture designed for complex multi-hop queries. The audience is enterprise developers; content is technical but jargon is always explained.

**Author:** Randy Park, Sr. Cloud Solution Architect

---

## Repository Structure

| File | Purpose |
|---|---|
| `Agentic.RAG.Pipeline.md` | Architecture reference — block diagram (Mermaid) + stage-by-stage description |
| `speech.v2.md` | Slide-by-slide presentation script with per-slide 📌 Glossary tables |

---

## Content Conventions

### Architecture Document (`Agentic.RAG.Pipeline.md`)
- Use **Mermaid `flowchart LR`** for pipeline diagrams; wrap subgraphs in `subgraph NAME["emoji Label"]` blocks.
- Pipeline stages follow this order: Input → Pre-processing → Planning → Vector Strategy → Retrieval → Reflect → Critique → Output → Evaluation.
- Describe each stage with a **role table** (`| Sub-Component | Role |`) and/or a numbered list.
- Key design principles live in a `### Key Design Principles` section at the bottom, as a bullet list.
- Source attribution goes at the very end in italics: `*Based on: Author — "Title" · Publication · Date*`

### Speech Script (`speech.v2.md`)
- Each slide section follows this exact structure:
  1. `## Slide N — Title`
  2. Spoken content in *italics* (first-person, conversational)
  3. `### 📌 Glossary — Slide N` with a `| Term | What it means |` table
  4. Two `---` separators between slides
- Speaker name in the header: `### Speaker: Randy Park | Sr. Cloud Solution Architect`
- Glossary entries define every new term introduced on that slide; plain-language, no jargon in the definitions.
- Tone: **direct, developer-focused, benefit-not-math**. Never explain the math; explain what the component *does for you*.

---

## Writing Style Guidelines

- **Architecture over intelligence** framing: RAG failures are architectural, not model limitations. Reinforce this throughout.
- Use table formatting (`| col | col |`) for comparison and decision logic (e.g., strategy selection, policy decisions).
- Avoid passive voice in slide speech — keep it active and conversational.
- When introducing a new pipeline concept, always pair it with a concrete real-world analogy (e.g., "like a researcher's notepad").
- The system is **cyclical, not linear** — when describing control flow, emphasize the loop and the policy agent's role in it.

---

## Key Domain Concepts

| Concept | One-liner |
|---|---|
| **RagState** | Per-workflow research state tracking the query, plan, findings, distilled evidence, step index, and iteration count |
| **KnowledgeBaseState** | Application-shared source status and description; actual document chunks remain in `VectorStore` |
| **Dual LLM strategy** | Powerful model for planning & synthesis; fast/cheap model for routine sub-tasks |
| **Cross Encoder reranker** | Re-ranks top-10 retrieved chunks jointly with the query; outputs high-precision top-3 |
| **Contextual Distillation** | Compresses top-3 chunks into a dense paragraph; reduces noise and token cost |
| **Metadata-aware chunking** | Tags each chunk with its document section so the retriever can restrict search scope |
| **Policy Agent** | Control-flow decision maker: Continue / Re-thinking / Finish |

---

## What Copilot Should and Should Not Do

**Do:**
- Follow existing slide/glossary formatting exactly when adding new slides.
- Maintain the Mermaid subgraph structure when updating the block diagram.
- Keep glossary definitions jargon-free and concise (1–2 sentences).
- Suggest new glossary terms for any new technical concept introduced.

**Do not:**
- Add deep mathematical explanations (this is a developer-practitioner, not researcher, audience).
- Remove the `---` separators between slides.
- Change the speaker attribution header.
- Embed content that belongs in one file into the other (link or reference instead).

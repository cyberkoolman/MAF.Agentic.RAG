using AgenticRAG.Models;

namespace AgenticRAG.Services;

/// <summary>
/// Selects the retrieval strategy for a given query.
/// </summary>
public enum SearchStrategy { Vector, Keyword, Hybrid }

/// <summary>
/// In-memory vector store supporting semantic (vector), lexical (keyword), and hybrid search.
/// Uses cosine similarity for vector search and term-overlap scoring for keyword search.
/// Hybrid search combines both via Reciprocal Rank Fusion (RRF).
/// </summary>
public class VectorStore
{
    private readonly List<RagDocument> _documents = new();

    /// <summary>Total number of indexed document chunks.</summary>
    public int DocumentCount => _documents.Count;

    /// <summary>Adds a batch of pre-embedded documents to the index.</summary>
    public void AddDocuments(IEnumerable<RagDocument> documents)
        => _documents.AddRange(documents);

    // ─────────────────────────────────────────────────────────────
    // Search methods
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Semantic (dense vector) search using cosine similarity.
    /// Best for conceptual or paraphrased queries.
    /// </summary>
    public List<RagDocument> VectorSearch(float[] queryEmbedding, int topK)
    {
        return _documents
            .Select(doc => (doc, score: CosineSimilarity(queryEmbedding, doc.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.doc with { RelevanceScore = x.score })
            .ToList();
    }

    /// <summary>
    /// Lexical (sparse) search using query-term overlap scoring.
    /// Best for exact names, numbers, or acronyms.
    /// </summary>
    public List<RagDocument> KeywordSearch(string query, int topK)
    {
        var terms = query.ToLowerInvariant()
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return _documents
            .Select(doc =>
            {
                var lowerContent = doc.Content.ToLowerInvariant();
                float score = (float)terms.Count(t => lowerContent.Contains(t)) / terms.Length;
                return (doc, score);
            })
            .Where(x => x.score > 0f)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.doc with { RelevanceScore = x.score })
            .ToList();
    }

    /// <summary>
    /// Hybrid search combining vector and keyword results via Reciprocal Rank Fusion.
    /// Balances recall (vector) with precision (keyword).
    /// </summary>
    public List<RagDocument> HybridSearch(string query, float[] queryEmbedding, int topK)
    {
        const int k = 60; // RRF constant — higher value dampens rank differences

        var vectorResults  = VectorSearch(queryEmbedding, topK * 2);
        var keywordResults = KeywordSearch(query, topK * 2);

        // Accumulate RRF scores per document id
        var scores = new Dictionary<string, float>();
        foreach (var (doc, rank) in vectorResults.Select((d, i) => (d, i)))
            scores[doc.Id] = scores.GetValueOrDefault(doc.Id) + 1f / (k + rank + 1);

        foreach (var (doc, rank) in keywordResults.Select((d, i) => (d, i)))
            scores[doc.Id] = scores.GetValueOrDefault(doc.Id) + 1f / (k + rank + 1);

        // Build lookup and return top-K by fused score
        var lookup = vectorResults.Concat(keywordResults).DistinctBy(d => d.Id).ToDictionary(d => d.Id);

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Where(kv => lookup.ContainsKey(kv.Key))
            .Select(kv => lookup[kv.Key] with { RelevanceScore = kv.Value })
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────
    // Math
    // ─────────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }
}

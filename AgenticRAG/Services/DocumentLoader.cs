using HtmlAgilityPack;
using AgenticRAG.Models;
using AgenticRAG.Configuration;
using System.Net;
using System.Text;

namespace AgenticRAG.Services;

/// <summary>
/// Loads documents from any number of configured sources (remote URLs or local files),
/// splits them into overlapping chunks with section metadata,
/// and generates embeddings ready for the VectorStore.
/// </summary>
public class DocumentLoader
{
    private readonly PipelineSettings _pipeline;
    private const int CharsPerToken = 4;

    public DocumentLoader(AppSettings settings)
    {
        _pipeline = settings.Pipeline;
    }

    // ─────────────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads, chunks, and embeds all configured document sources.
    /// Returns a flat list of RagDocuments ready for the VectorStore.
    /// </summary>
    public async Task<List<RagDocument>> LoadAllAsync(
        IEnumerable<DocumentSource> sources,
        AzureAIService aiService,
        CancellationToken cancellationToken = default)
    {
        var all = new List<RagDocument>();

        foreach (var source in sources)
        {
            Console.WriteLine($"  Loading: {source.Name}");
            var docs = await LoadSourceAsync(source, aiService, cancellationToken);
            all.AddRange(docs);
            Console.WriteLine($"  → {docs.Count} chunks from '{source.Name}'");
        }

        return all;
    }

    // ─────────────────────────────────────────────────────────────
    // Per-source loading
    // ─────────────────────────────────────────────────────────────

    private async Task<List<RagDocument>> LoadSourceAsync(
        DocumentSource source,
        AzureAIService aiService,
        CancellationToken cancellationToken)
    {
        string rawContent = await FetchContentAsync(source, cancellationToken);

        var chunks = source.Type.ToLowerInvariant() == "text"
            ? ChunkPlainText(rawContent, source.Name)
            : ChunkHtml(rawContent, source.Name);

        Console.WriteLine($"    Created {chunks.Count} chunks, generating embeddings...");
        return await EmbedChunksAsync(chunks, source, aiService, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Fetch: URL or local file
    // ─────────────────────────────────────────────────────────────

    private static async Task<string> FetchContentAsync(
        DocumentSource source,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            return await File.ReadAllTextAsync(source.FilePath, ct);
        }

        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            using var http = new HttpClient();
            // TryAddWithoutValidation bypasses strict token validation for email in User-Agent
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "AgenticRAG Research Bot research@example.com");
            return await http.GetStringAsync(source.Url, ct);
        }

        throw new InvalidOperationException(
            $"DocumentSource '{source.Name}' must have either a Url or FilePath configured.");
    }

    // ─────────────────────────────────────────────────────────────
    // Chunking: HTML
    // ─────────────────────────────────────────────────────────────

    private List<(string Content, string Section)> ChunkHtml(string html, string sourceName)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        RemoveNodes(doc, "//script", "//style", "//head", "//nav", "//footer");

        var chunks         = new List<(string, string)>();
        var currentSection = sourceName;
        var buffer         = new StringBuilder();
        int chunkChars     = _pipeline.ChunkSize    * CharsPerToken;
        int overlapChars   = _pipeline.ChunkOverlap * CharsPerToken;

        foreach (var node in doc.DocumentNode.DescendantsAndSelf())
        {
            if (node.NodeType != HtmlNodeType.Text) continue;

            var text = WebUtility.HtmlDecode(node.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Detect section headers (e.g. "Item 1A. Risk Factors" in SEC filings,
            // or any short line starting with a capital word in other HTML docs)
            if (IsSectionHeader(text))
                currentSection = text.Length > 100 ? text[..100] : text;

            buffer.Append(' ').Append(text);

            if (buffer.Length >= chunkChars * 2)
                FlushBuffer(buffer, currentSection, chunkChars, overlapChars, chunks);
        }

        if (buffer.Length > 100)
            FlushBuffer(buffer, currentSection, chunkChars, overlapChars, chunks);

        return chunks;
    }

    // ─────────────────────────────────────────────────────────────
    // Chunking: plain text
    // ─────────────────────────────────────────────────────────────

    private List<(string Content, string Section)> ChunkPlainText(string text, string sourceName)
    {
        var chunks      = new List<(string, string)>();
        var buffer      = new StringBuilder(text);
        int chunkChars  = _pipeline.ChunkSize    * CharsPerToken;
        int overlapChars = _pipeline.ChunkOverlap * CharsPerToken;

        FlushBuffer(buffer, sourceName, chunkChars, overlapChars, chunks);
        return chunks;
    }

    // ─────────────────────────────────────────────────────────────
    // Buffer → chunks
    // ─────────────────────────────────────────────────────────────

    private static void FlushBuffer(
        StringBuilder buffer,
        string section,
        int chunkSize,
        int overlap,
        List<(string, string)> chunks)
    {
        var text  = buffer.ToString().Trim();
        int start = 0;

        while (start < text.Length)
        {
            int end   = Math.Min(start + chunkSize, text.Length);
            var chunk = text[start..end].Trim();
            if (chunk.Length > 80)
                chunks.Add((chunk, section));
            start += chunkSize - overlap;
        }

        // Keep overlap tail for context continuity across flush boundaries
        if (text.Length > overlap)
        {
            buffer.Clear().Append(text[^overlap..]);
        }
        else
        {
            buffer.Clear();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Embed in batches
    // ─────────────────────────────────────────────────────────────

    private async Task<List<RagDocument>> EmbedChunksAsync(
        List<(string Content, string Section)> chunks,
        DocumentSource source,
        AzureAIService aiService,
        CancellationToken cancellationToken)
    {
        const int BatchSize = 16;
        var documents = new List<RagDocument>(chunks.Count);

        for (int i = 0; i < chunks.Count; i += BatchSize)
        {
            var batch  = chunks.Skip(i).Take(BatchSize).ToList();
            var embeds = await aiService.GenerateEmbeddingsAsync(
                batch.Select(c => c.Content), cancellationToken);

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

            int done = Math.Min(i + BatchSize, chunks.Count);
            Console.WriteLine($"    Embedded {done}/{chunks.Count}...");
        }

        return documents;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static bool IsSectionHeader(string text) =>
        text.Length < 120 &&
        (
            // SEC filing item headers: "Item 1A. Risk Factors"
            (text.StartsWith("Item ", StringComparison.OrdinalIgnoreCase) && char.IsDigit(text.ElementAtOrDefault(5))) ||
            // Generic: short all-caps or title-case headings (e.g. "EXECUTIVE SUMMARY", "Introduction")
            (text.Length < 80 && text == text.ToUpperInvariant() && text.Any(char.IsLetter))
        );

    private static void RemoveNodes(HtmlDocument doc, params string[] xpaths)
    {
        foreach (var xpath in xpaths)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes is null) continue;
            foreach (var n in nodes.ToList()) n.Remove();
        }
    }
}

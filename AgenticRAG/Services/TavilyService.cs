using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgenticRAG.Models;
using AgenticRAG.Configuration;

namespace AgenticRAG.Services;

/// <summary>
/// Wraps the Tavily Search API — an LLM-optimised search engine that returns
/// clean, ad-free results well-suited for RAG pipelines.
/// </summary>
public class TavilyService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    private const string SearchEndpoint = "https://api.tavily.com/search";

    public TavilyService(AppSettings settings, HttpClient? http = null)
    {
        _apiKey = settings.Tavily.ApiKey;
        _http   = http ?? new HttpClient();
    }

    /// <summary>
    /// Executes a web search and converts results to RagDocument objects so they
    /// flow seamlessly through the same retrieval funnel as internal documents.
    /// </summary>
    /// <param name="query">Optimised search query from the QueryRewriter agent.</param>
    /// <param name="maxResults">Number of results to request (Tavily default is 5).</param>
    public async Task<List<RagDocument>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var payload = new TavilyRequest
        {
            ApiKey      = _apiKey,
            Query       = query,
            SearchDepth = "advanced",
            MaxResults  = maxResults,
            IncludeRawContent = false
        };

        var response = await _http.PostAsJsonAsync(SearchEndpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<TavilyResponse>(cancellationToken: cancellationToken);

        return result?.Results?
            .Select(r => new RagDocument
            {
                Content = BuildContent(r),
                Source  = r.Title ?? "Web",
                Section = "Web Search Result",
                Url     = r.Url ?? ""
            })
            .ToList()
            ?? new List<RagDocument>();
    }

    private static string BuildContent(TavilyResult r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Title))   parts.Add($"Title: {r.Title}");
        if (!string.IsNullOrWhiteSpace(r.Content)) parts.Add(r.Content);
        else if (!string.IsNullOrWhiteSpace(r.Snippet)) parts.Add(r.Snippet);
        return string.Join("\n", parts);
    }

    // ─────────────────────────────────────────────────────────────
    // Private DTOs
    // ─────────────────────────────────────────────────────────────

    private class TavilyRequest
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = "";

        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("search_depth")]
        public string SearchDepth { get; set; } = "basic";

        [JsonPropertyName("max_results")]
        public int MaxResults { get; set; } = 5;

        [JsonPropertyName("include_raw_content")]
        public bool IncludeRawContent { get; set; } = false;
    }

    private class TavilyResponse
    {
        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
    }
}

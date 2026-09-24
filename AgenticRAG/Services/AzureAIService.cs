using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using AgenticRAG.Configuration;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.Text.Json;

namespace AgenticRAG.Services;

/// <summary>
/// Wraps Azure AI Foundry / Azure OpenAI for chat completions and text embeddings.
/// Reads endpoint and API key from AppSettings so credentials never appear in code.
/// </summary>
public class AzureAIService
{
    private readonly AzureOpenAIClient _client;
    private readonly AzureAISettings _settings;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AzureAIService(AppSettings settings)
    {
        _settings = settings.AzureAI;

        // Use API key when provided; fall back to DefaultAzureCredential (az login / managed identity).
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _client = new AzureOpenAIClient(
                new Uri(_settings.Endpoint),
                new AzureKeyCredential(_settings.ApiKey));
        }
        else
        {
            _client = new AzureOpenAIClient(
                new Uri(_settings.Endpoint),
                new DefaultAzureCredential());
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Chat completions
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a single system + user message pair and returns the assistant's text with token usage.
    /// </summary>
    /// <param name="useReasoningModel">
    /// True → use the high-capability reasoning model (planning, policy, synthesis).
    /// False → use the faster model (rewriting, reranking, distillation, reflection).
    /// </param>
    public async Task<CompletionResult> CompleteAsync(
        string systemPrompt,
        string userMessage,
        bool useReasoningModel = false,
        CancellationToken cancellationToken = default)
    {
        var model = useReasoningModel ? _settings.ReasoningModel : _settings.FastModel;
        var chatClient = _client.GetChatClient(model);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userMessage)
        };

        var response = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var text = response.Value.Content[0].Text;
        var usage = response.Value.Usage;

        return new CompletionResult(
            text,
            usage?.InputTokenCount ?? 0,
            usage?.OutputTokenCount ?? 0,
            usage?.TotalTokenCount ?? 0);
    }

    /// <summary>
    /// Like CompleteAsync but deserialises the response JSON into <typeparamref name="T"/>.
    /// The system prompt is automatically appended with a JSON-only instruction.
    /// </summary>
    public async Task<T?> CompleteStructuredAsync<T>(
        string systemPrompt,
        string userMessage,
        bool useReasoningModel = false,
        CancellationToken cancellationToken = default)
    {
        const string jsonSuffix =
            "\n\nIMPORTANT: Respond with valid JSON only. No markdown fences, no explanations — just the JSON object.";

        var result = await CompleteAsync(
            systemPrompt + jsonSuffix,
            userMessage,
            useReasoningModel,
            cancellationToken);

        var json = StripMarkdownFences(result.Text);

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[AzureAIService] JSON parse error: {ex.Message}\nRaw: {json[..Math.Min(200, json.Length)]}");
            return default;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Embeddings
    // ─────────────────────────────────────────────────────────────

    /// <summary>Generates a single embedding vector for the given text.</summary>
    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var embeddingClient = _client.GetEmbeddingClient(_settings.EmbeddingModel);
            var response = await embeddingClient.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken);
            return response.Value.ToFloats().ToArray();
        }
        catch (System.ClientModel.ClientResultException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{_settings.EmbeddingModel}' not found. " +
                "Check the 'AzureAI:EmbeddingModel' value in appsettings.json matches " +
                "an active deployment in your Azure AI Foundry resource.", ex);
        }
    }

    /// <summary>
    /// Generates embeddings for a batch of texts in a single API call.
    /// Results are returned in the same order as the input list.
    /// </summary>
    public async Task<List<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var embeddingClient = _client.GetEmbeddingClient(_settings.EmbeddingModel);
            var textList = texts.ToList();
            var response = await embeddingClient.GenerateEmbeddingsAsync(textList, cancellationToken: cancellationToken);
            return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
        }
        catch (System.ClientModel.ClientResultException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{_settings.EmbeddingModel}' not found. " +
                "Check the 'AzureAI:EmbeddingModel' value in appsettings.json matches " +
                "an active deployment in your Azure AI Foundry resource.", ex);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;

        // Remove opening fence line (e.g., ```json)
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var withoutOpen = trimmed[(firstNewline + 1)..];

        // Remove closing fence
        var lastFence = withoutOpen.LastIndexOf("```");
        if (lastFence >= 0)
            withoutOpen = withoutOpen[..lastFence];

        return withoutOpen.Trim();
    }
}

/// <summary>Chat completion result with token usage from the LLM.</summary>
public record CompletionResult(string Text, int InputTokens, int OutputTokens, int TotalTokens);

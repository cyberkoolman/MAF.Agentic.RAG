// Copyright (c) Microsoft. All rights reserved.
// Agentic RAG — DevUI Chat Window

using AgenticRAG.Api;
using AgenticRAG.Configuration;
using AgenticRAG.Models;
using AgenticRAG.Services;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.Extensions.AI;

Console.OutputEncoding = System.Text.Encoding.UTF8;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Load configuration
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json",       optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true,  reloadOnChange: false);

builder.Services.AddHttpClient().AddLogging();

// ─── Register RAG pipelines as two separate agents ───────────────────────────

var settings = builder.Configuration.Get<AppSettings>()
    ?? throw new InvalidOperationException("Failed to load appsettings.");

// Shared services so documents loaded in one agent are visible to the other
var ai     = new AzureAIService(settings);
var store  = new VectorStore();
var tavily = new TavilyService(settings);
var loader = new DocumentLoader(settings);
var kbState = new KnowledgeBaseState();

var agenticClient = new RagWorkflowChatClient(settings, ai, store, tavily, loader, kbState, useOneShot: false);
var oneShotClient = new RagWorkflowChatClient(settings, ai, store, tavily, loader, kbState, useOneShot: true);

builder.AddAIAgent(
    name:         "Agentic-RAG",
    instructions: "You are a deep-thinking research assistant powered by an Agentic RAG pipeline. " +
                  "You decompose complex queries into multi-step plans, retrieve and reflect iteratively, " +
                  "and synthesize comprehensive answers.",
    chatClient:   agenticClient);

builder.AddAIAgent(
    name:         "OneShot-RAG",
    instructions: "You are a simple RAG assistant that answers questions in a single pass. " +
                  "You retrieve relevant documents and answer directly without planning or reflection.",
    chatClient:   oneShotClient);

// ─── DevUI services ───────────────────────────────────────────────────────────

builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();
builder.Services.AddDevUI();

// ─── Build and map ────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

app.MapOpenAIResponses();
app.MapOpenAIConversations();
app.MapDevUI();

Console.WriteLine(new string('═', 60));
Console.WriteLine("  Agentic RAG — DevUI Chat");
Console.WriteLine("  http://localhost:8888/devui");
Console.WriteLine(new string('═', 60));

await app.RunAsync();

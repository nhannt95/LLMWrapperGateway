using LLMWrapperGateway.Data;
using LLMWrapperGateway.Helpers;
using LLMWrapperGateway.Middleware;
using LLMWrapperGateway.Models;
using LLMWrapperGateway.Services;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
builder.Services.AddDbContext<WrapperDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// ── Services ──
builder.Services.AddScoped<WrapperManager>();
builder.Services.AddHttpClient("LLMProxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

// ── Swagger / OpenAPI ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LLM Wrapper Gateway", Version = "v1" });
});

var app = builder.Build();

// ── Auto-migrate on startup ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WrapperDbContext>();
    await db.Database.MigrateAsync();
}

// ── Middleware ──
app.UseSwagger();
app.UseSwaggerUI();
app.UseMiddleware<WrapperAuthMiddleware>();

// ════════════════════════════════════════════════════════════════
//  Management API endpoints (no auth required)
// ════════════════════════════════════════════════════════════════

app.MapPost("/api/wrappers", async (CreateWrapperRequest req, WrapperManager mgr) =>
{
    var wrapper = await mgr.CreateAsync(req);
    return Results.Created($"/api/wrappers/{wrapper.Id}", wrapper);
})
.WithName("CreateWrapper")
.WithOpenApi();

app.MapGet("/api/wrappers", async (WrapperManager mgr) =>
{
    return Results.Ok(await mgr.ListAsync());
})
.WithName("ListWrappers")
.WithOpenApi();

app.MapGet("/api/wrappers/{id:guid}", async (Guid id, WrapperManager mgr) =>
{
    var wrapper = await mgr.GetByIdAsync(id);
    return wrapper is not null ? Results.Ok(wrapper) : Results.NotFound();
})
.WithName("GetWrapper")
.WithOpenApi();

app.MapDelete("/api/wrappers/{id:guid}", async (Guid id, WrapperManager mgr) =>
{
    return await mgr.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteWrapper")
.WithOpenApi();

// ════════════════════════════════════════════════════════════════
//  Wrapper Proxy – forwards to upstream LLM provider
//  Route: /w/{wrapperId}/{**path}
//  Supports: /v1/chat/completions, /v1/models, /v1/embeddings, etc.
// ════════════════════════════════════════════════════════════════

app.Map("/w/{wrapperId}/{**path}", async (
    HttpContext context,
    Guid wrapperId,
    string path,
    WrapperManager mgr,
    IHttpClientFactory httpClientFactory) =>
{
    // ── 1. Load wrapper config ──
    var clientApiKey = context.Items["ClientApiKey"] as string;
    var wrapper = await mgr.GetByIdAsync(wrapperId);

    if (wrapper is null)
        return Results.NotFound(new { error = "Wrapper not found" });

    // ── 2. Read request body ──
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var requestBody = await reader.ReadToEndAsync();

    // ── 3. Parse stream flag từ body client ──
    bool isStreaming = false;
    if (!string.IsNullOrEmpty(requestBody))
    {
        try
        {
            using var parseDoc = JsonDocument.Parse(requestBody);
            if (parseDoc.RootElement.TryGetProperty("stream", out var streamProp))
                isStreaming = streamProp.ValueKind == JsonValueKind.True;
        }
        catch { /* not JSON, ignore */ }
    }

    // ── 4. Build upstream URL (company API cần ?stream= ở URL) ──
    var upstreamUrl = BuildUpstreamUrl(wrapper, path, isStreaming);

    // ── 5. Transform request body if mapping is configured ──
    var extraVars = new Dictionary<string, string>
    {
        ["session"] = wrapper.Session ?? ""
    };

    var transformedBody = JsonMappingHelper.ApplyMapping(
        wrapper.RequestMapping, requestBody, extraVars);

    // ── 6. Forward request to upstream ──
    var client = httpClientFactory.CreateClient("LLMProxy");
    var upstreamRequest = new HttpRequestMessage(
        new HttpMethod(context.Request.Method), upstreamUrl);

    if (!string.IsNullOrEmpty(transformedBody) && context.Request.Method != "GET")
    {
        upstreamRequest.Content = new StringContent(transformedBody, Encoding.UTF8, "application/json");
    }

    // Client gửi "api-key" → forward thành "x-api-key" cho company
    upstreamRequest.Headers.TryAddWithoutValidation("x-api-key", clientApiKey!);

    // Forward Accept header
    if (context.Request.Headers.ContainsKey("Accept"))
        upstreamRequest.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToArray());

    HttpResponseMessage upstreamResponse;
    try
    {
        upstreamResponse = isStreaming
            ? await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead)
            : await client.SendAsync(upstreamRequest);
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(new { error = "Upstream request failed", detail = ex.Message }, statusCode: 502);
    }

    // ── 7. Stream response or transform and return ──
    if (isStreaming)
    {
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync();
        await upstreamStream.CopyToAsync(context.Response.Body);
        return Results.Empty;
    }

    var responseBody = await upstreamResponse.Content.ReadAsStringAsync();

    // Extract text từ response company theo ResponsePath, wrap thành format OpenAI
    string finalResponse;
    if (!string.IsNullOrEmpty(wrapper.ResponsePath))
    {
        var content = JsonMappingHelper.ExtractByPath(responseBody, wrapper.ResponsePath) ?? "";
        finalResponse = BuildOpenAiResponse(content);
    }
    else
    {
        // Không có ResponsePath → trả nguyên response company
        finalResponse = responseBody;
    }

    context.Response.StatusCode = (int)upstreamResponse.StatusCode;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(finalResponse);
    return Results.Empty;
})
.WithName("WrapperProxy")
.WithOpenApi();

app.Run();

// ── Helper: build hardcoded OpenAI response format ──
static string BuildOpenAiResponse(string content)
{
    var escaped = content
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");

    return $$"""
    {
      "id": "chatcmpl-{{Guid.NewGuid():N}}",
      "object": "chat.completion",
      "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
      "choices": [
        {
          "index": 0,
          "message": {
            "role": "assistant",
            "content": "{{escaped}}"
          },
          "finish_reason": "stop"
        }
      ],
      "usage": {
        "prompt_tokens": 0,
        "completion_tokens": 0,
        "total_tokens": 0
      }
    }
    """;
}

// ── Helper: build upstream URL ──
// Company API (có Session): {BaseUrl}/v1/{SessionId}?stream=true/false
// Standard (Ollama/OpenAI):  {BaseUrl}/{path}
static string BuildUpstreamUrl(WrapperConfig wrapper, string path, bool isStreaming)
{
    var sb = new StringBuilder(wrapper.BaseUrl);

    if (!string.IsNullOrEmpty(wrapper.Session))
    {
        // Company API: stream nằm ở query string, không nằm trong body
        sb.Append("/v1/");
        sb.Append(wrapper.Session);
        sb.Append("?stream=");
        sb.Append(isStreaming ? "true" : "false");
    }
    else
    {
        // Standard provider (OpenAI/Ollama): forward path as-is
        sb.Append('/');
        sb.Append(path);
    }

    return sb.ToString();
}

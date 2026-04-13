namespace LLMWrapperGateway.Middleware;

public class WrapperAuthMiddleware
{
    private readonly RequestDelegate _next;

    public WrapperAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only apply to proxy routes (/w/...)
        if (!context.Request.Path.StartsWithSegments("/w"))
        {
            await _next(context);
            return;
        }

        // Client phải gửi header "api-key"
        if (!context.Request.Headers.TryGetValue("api-key", out var apiKey) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":"Missing or empty api-key header"}""");
            return;
        }

        // Lưu api-key để proxy handler forward sang x-api-key cho company
        context.Items["ClientApiKey"] = apiKey.ToString();
        await _next(context);
    }
}

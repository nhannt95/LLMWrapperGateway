namespace LLMWrapperGateway.Models;

public class WrapperConfig
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "local"; // "local", "ollama", "openai"
    public string BaseUrl { get; set; } = string.Empty;
    public string? Session { get; set; }
    public string? RequestMapping { get; set; }
    public string? ResponsePath { get; set; } // path tới text trong response company, vd: "result.output"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CreateWrapperRequest
{
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "local";
    public string BaseUrl { get; set; } = string.Empty;
    public string? Session { get; set; }
    public string? RequestMapping { get; set; }
    public string? ResponsePath { get; set; }
}

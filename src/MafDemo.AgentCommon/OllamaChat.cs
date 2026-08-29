using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;

namespace MafDemo.AgentCommon;

/// <summary>
/// Factory for an Ollama-backed <see cref="IChatClient"/>.
/// Reused by P02+ agent projects. Endpoint and model come from
/// <c>appsettings.json</c> (<c>Ollama:Endpoint</c> / <c>Ollama:Model</c>),
/// overridden by the OLLAMA_ENDPOINT / OLLAMA_MODEL environment variables
/// and with hardcoded local defaults as the last resort.
/// </summary>
public static class OllamaChat
{
    // Built once and cached: the file ships next to the binary and cannot
    // change while the process runs, so re-reading it per Create() call
    // would only add file I/O to the P02+ reuse surface.
    private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    public static IChatClient Create(string? model = null)
    {
        var endpoint = Resolve("OLLAMA_ENDPOINT", "Ollama:Endpoint", "http://localhost:11434");
        model ??= Resolve("OLLAMA_MODEL", "Ollama:Model", "glm-5.3-flash:cloud");

        // OpenTelemetry instrumentation per the MAF observability doc: the
        // source name passed here must match the AddSource name in Telemetry.
        // (Typed as IChatClient so AsBuilder() binds to the chat-client builder;
        // OllamaApiClient also implements IEmbeddingGenerator, which is ambiguous otherwise.)
        IChatClient client = new OllamaApiClient(new Uri(endpoint), model);
        return client.AsBuilder()
            .UseOpenTelemetry(sourceName: Telemetry.SourceName)
            .Build();
    }

    /// <summary>
    /// Precedence: environment variable, then appsettings.json, then fallback.
    /// A whitespace-only env value counts as unset — "" would otherwise slip
    /// past a null check and crash <see cref="Uri"/> construction.
    /// </summary>
    private static string Resolve(string envVar, string configKey, string fallback)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        var configured = Config[configKey];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured!;
    }
}

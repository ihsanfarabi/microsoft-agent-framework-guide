using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MafDemo.AgentCommon;

/// <summary>
/// OpenTelemetry wiring for the console agent: a <see cref="TracerProvider"/>
/// with a console exporter, listening on the source name Microsoft Agent
/// Framework instruments chat clients and agents with.
/// Per https://learn.microsoft.com/en-us/agent-framework/agents/observability,
/// the source name defaults to <c>Experimental.Microsoft.Agents.AI</c> when
/// none is passed to <c>UseOpenTelemetry</c>, so that exact name must be
/// registered with <c>AddSource</c> for the spans to reach the exporter.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "Experimental.Microsoft.Agents.AI";

    /// <summary>Builds and starts the trace provider. Dispose to flush spans.</summary>
    public static TracerProvider Start(string serviceName) =>
        Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
            .AddSource(SourceName)
            .AddConsoleExporter()
            .Build();

    /// <summary>
    /// Default OTLP (gRPC) receiver the Aspire dashboard container exposes:
    /// the standalone image listens on 18889 inside the container, and
    /// <c>aspire-dashboard.sh</c> maps host 4317 to it.
    /// </summary>
    public const string DefaultOtlpEndpoint = "http://localhost:4317";

    /// <summary>
    /// Builds and starts a trace provider that exports over OTLP (gRPC)
    /// instead of to the console — the target is the standalone Aspire
    /// dashboard (started with <c>aspire-dashboard.sh</c>). The endpoint
    /// defaults to <see cref="DefaultOtlpEndpoint"/> and is overridable via
    /// the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable.
    /// Dispose to flush spans.
    /// </summary>
    public static TracerProvider StartOtlp(string serviceName) =>
        Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
            .AddSource(SourceName)
            .AddOtlpExporter(o => o.Endpoint = new Uri(
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
                    ?? DefaultOtlpEndpoint))
            .Build();
}

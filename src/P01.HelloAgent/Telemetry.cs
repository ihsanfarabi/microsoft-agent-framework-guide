using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace P01.HelloAgent;

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
    public static TracerProvider Start() =>
        Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("P01.HelloAgent"))
            .AddSource(SourceName)
            .AddConsoleExporter()
            .Build();
}

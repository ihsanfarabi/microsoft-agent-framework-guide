#!/usr/bin/env bash
# Starts the standalone Aspire dashboard to receive OTLP telemetry from the
# demo agents (see Telemetry.StartOtlp in src/MafDemo.AgentCommon/Telemetry.cs).
#
# Port mapping:
#   18888 -> 18888 : dashboard UI at http://localhost:18888 (Traces view)
#   4317  -> 18889 : OTLP gRPC endpoint the agents export to
#                   (http://localhost:4317, override with OTEL_EXPORTER_OTLP_ENDPOINT)
#
# Stop it with: docker stop aspire-dashboard
docker run --rm -d \
  -p 18888:18888 \
  -p 4317:18889 \
  --name aspire-dashboard \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest

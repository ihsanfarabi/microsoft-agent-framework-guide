# Multi-stage build for the HelpDeskHQ capstone app. Build context is the
# repo root (docker-compose.yml sets `context: .`): project references span
# src/, and docs/corpus ships into the runtime image for the RAG index.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/P10.HelpDeskCapstone -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The handbook corpus must sit at <approot>/docs/corpus so the startup
# walk-up search finds it (MafDemo.Core HandbookCorpus.Locate). The agents/
# YAML is copied explicitly — it is loaded from BaseDirectory/agents at
# runtime and publish does not always carry the None-copy items.
COPY --from=build /src/docs/corpus /app/docs/corpus
COPY --from=build /src/src/P10.HelpDeskCapstone/Definitions /app/Definitions
ENV ASPNETCORE_URLS=http://+:8080
ENV OLLAMA_ENDPOINT=http://host.docker.internal:11434
EXPOSE 8080
ENTRYPOINT ["dotnet", "P10.HelpDeskCapstone.dll"]
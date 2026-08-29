using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P04.HandbookRag;

/// <summary>
/// HelpDeskHQ handbook bot: a <see cref="ChatClientAgent"/> on the shared
/// Ollama chat factory whose grounding comes from <see cref="HandbookContextProvider"/>
/// — the provider runs on every invocation and injects the retrieved handbook
/// chunks, so the agent itself stays a plain chat agent.
/// </summary>
public static class HandbookBot
{
    public static ChatClientAgent Create(HandbookRetriever retriever)
        => new(OllamaChat.Create(), new ChatClientAgentOptions
        {
            Name = "HandbookBot",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's handbook bot. Answer ONLY from the provided handbook excerpts.
                    Cite the doc filename in square brackets, like [onboarding.md], for every fact you use.
                    If the excerpts do not answer the question, say exactly: 'That is not in the handbook.'
                    Do not use any knowledge that is not in the excerpts.
                    """,
            },
            AIContextProviders = [new HandbookContextProvider(retriever)],
        });
}

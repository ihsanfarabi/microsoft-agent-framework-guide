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
/// <see cref="CreateToolVariant"/> is the Task 5 alternative: same grounding
/// rules, but retrieval is a <c>search_handbook</c> tool the model must
/// choose to call.
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

    /// <summary>
    /// Tool-based retrieval variant: no context provider — the model gets a
    /// <c>search_handbook</c> tool wrapping the same <see cref="HandbookRetriever"/>
    /// and decides when to retrieve. The functions instance is passed in (not
    /// built here) so the caller can read <see cref="HandbookToolFunctions.SearchCount"/>
    /// to observe whether/how often the model actually searched.
    /// </summary>
    public static ChatClientAgent CreateToolVariant(HandbookToolFunctions functions)
    {
        // UseFunctionInvocation is required (P02 TicketBot pattern): it runs the
        // client-side tool loop (model requests search_handbook -> retriever
        // executes -> excerpts go back to the model) that a raw
        // OllamaChat.Create() client will not provide.
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .UseFunctionInvocation()
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "HandbookToolBot",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's handbook bot. Answer ONLY from handbook excerpts
                    retrieved with the search_handbook tool.
                    Cite the doc filename in square brackets, like [onboarding.md], for every fact you use.
                    If the excerpts do not answer the question, say exactly: 'That is not in the handbook.'
                    Do not use any knowledge that is not in the excerpts.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(
                        functions.SearchHandbookAsync,
                        name: "search_handbook",
                        description:
                            "Search the company IT handbook for policy facts. Returns cited excerpts "
                            + "formatted as [doc #index] blocks."),
                ],
            },
        });
    }
}

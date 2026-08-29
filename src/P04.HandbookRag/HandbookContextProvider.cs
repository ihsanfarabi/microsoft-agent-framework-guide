using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafDemo.Core.Handbook;

namespace P04.HandbookRag;

/// <summary>
/// RAG context provider: on each agent invocation it embeds the latest user
/// message, retrieves the closest handbook chunks from the in-memory index,
/// and hands them to the model as additional context. The default
/// <see cref="AIContextProvider"/> merge behavior stamps these messages with
/// the AIContextProvider source and concatenates them ahead of the input
/// messages, so the model sees the excerpts before the question.
/// </summary>
public class HandbookContextProvider(HandbookRetriever retriever) : AIContextProvider
{
    private const int TopK = 3;

    /// <summary>
    /// Shipped 1.19.0 shape (verified against Microsoft.Agents.AI.Abstractions):
    /// protected virtual ValueTask&lt;AIContext&gt; ProvideAIContextAsync(
    /// AIContextProvider.InvokingContext, CancellationToken). The context exposes
    /// the caller's input through <c>context.AIContext.Messages</c>.
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken)
    {
        var latestUser = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User);
        if (latestUser is null)
            return new AIContext();

        var hits = await retriever.SearchAsync(latestUser.Text, topK: TopK);
        if (hits.Count == 0)
            return new AIContext();

        var handbook = string.Join("\n---\n", hits.Select(h => $"[{h.Doc} #{h.Index}]\n{h.Text}"));
        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User,
                "Company IT handbook excerpts (cite the [doc] you use):\n" + handbook)],
        };
    }
}

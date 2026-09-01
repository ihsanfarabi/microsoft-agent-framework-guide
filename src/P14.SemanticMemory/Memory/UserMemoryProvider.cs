using MafDemo.AgentCommon;
using MafDemo.Core.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace P14.SemanticMemory.Memory;

/// <summary>
/// Fact-extraction memory provider (P14 Task 3): at the end of every agent run
/// it asks a tiny extractor agent for durable third-person facts about the user
/// and upserts them into a <see cref="FactMemoryStore"/>; at the start of every
/// run it recalls the stored facts most similar to the latest external user
/// message and injects them as an additional context message.
/// </summary>
/// <remarks>
/// Real Microsoft.Agents.AI 1.19.0 base signatures (verified by decompiling the
/// installed Microsoft.Agents.AI.Abstractions.dll; the research notes the plan
/// was written from used older names):
/// <c>protected virtual ValueTask&lt;AIContext&gt; ProvideAIContextAsync(AIContextProvider.InvokingContext, CancellationToken = default)</c>
/// and
/// <c>protected virtual ValueTask StoreAIContextAsync(AIContextProvider.InvokedContext, CancellationToken = default)</c>.
/// The base's default <c>InvokedCoreAsync</c> already filters request messages to
/// <see cref="AgentRequestMessageSourceType.External"/> before calling this
/// override, but the filter is applied here as well so the override stays
/// correct when invoked directly.
/// <para>
/// Dedupe (cosine ≥ 0.9, user-scoped in-place upsert) is owned entirely by
/// <see cref="FactMemoryStore.AddAsync"/> — this provider never compares texts.
/// Recall and extraction failures are logged and swallowed: memory must never
/// break the main agent run.
/// </para>
/// </remarks>
public sealed class UserMemoryProvider : AIContextProvider
{
    /// <summary>Default user scope, matching the P14 chat-history baseline.</summary>
    public const string DefaultUserId = "demo-user";

    private readonly FactMemoryStore _facts;
    private readonly IFactExtractor _extractor;
    private readonly string _userId;
    private readonly ILogger _logger;

    public UserMemoryProvider(
        FactMemoryStore facts,
        string? userId = null,
        IFactExtractor? extractor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _userId = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId!;
        _extractor = extractor ?? new ChatClientFactExtractor(OllamaChat.Create());
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<UserMemoryProvider>();
    }

    /// <summary>
    /// Recalls the facts of the configured user most similar to the latest
    /// external user message and returns them as one injected system message
    /// (merged ahead of the caller's input by the base class, which also stamps
    /// it with the AIContextProvider source attribution).
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latestUser = (context.AIContext.Messages ?? []).LastOrDefault(IsExternalUserMessage);
            if (string.IsNullOrWhiteSpace(latestUser?.Text))
            {
                return new AIContext();
            }

            var recalled = await _facts.RecallAsync(_userId, latestUser.Text);
            if (recalled.Count == 0)
            {
                return new AIContext();
            }

            var summary = string.Join("\n", recalled.Select(f => $"- {f.Text}"));
            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System,
                    $"Remembered facts about the user (may be incomplete — verify before relying on them):\n{summary}")],
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserMemoryProvider recall failed; continuing without memory context.");
            return new AIContext();
        }
    }

    /// <summary>
    /// Extracts facts from the conversation turn via the injected extractor and
    /// adds them to the store under the configured user id. An empty extraction,
    /// malformed JSON, or a failing extractor all result in a no-op.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(
        AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latestUser = (context.RequestMessages ?? []).LastOrDefault(IsExternalUserMessage);
            if (string.IsNullOrWhiteSpace(latestUser?.Text))
            {
                return;
            }

            // Extract from the turn: the user's message plus whatever the agent
            // answered (the reply often restates the preference usefully).
            var turn = new List<ChatMessage> { latestUser };
            if (context.ResponseMessages is { } responses)
            {
                turn.AddRange(responses);
            }

            var facts = await _extractor.ExtractFactsAsync(turn, cancellationToken);
            var stored = 0;
            foreach (var fact in facts)
            {
                if (string.IsNullOrWhiteSpace(fact))
                {
                    continue;
                }

                // Dedupe (cosine >= 0.9, user-scoped upsert) is the store's job.
                await _facts.AddAsync(_userId, fact.Trim());
                stored++;
            }

            if (stored > 0)
            {
                _logger.LogInformation("UserMemoryProvider stored {Count} fact(s) for user {UserId}.", stored, _userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserMemoryProvider fact storage failed; the agent run is unaffected.");
        }
    }

    // Applied explicitly (in addition to the base's default filter) so the
    // override is self-contained when StoreAIContextAsync is called directly.
    private static bool IsExternalUserMessage(ChatMessage message) =>
        message.Role == ChatRole.User
        && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External;
}

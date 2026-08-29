using System.Text.Json;
using Microsoft.Agents.AI;

namespace P03.SessionChat;

/// <summary>
/// Saves and restores <see cref="AgentSession"/> instances under threads/ so a
/// conversation survives a process restart. Built on the framework's own session
/// serialization: in Microsoft.Agents.AI 1.19.0 both directions are async and
/// <see cref="JsonElement"/>-based (the doc page's sync <c>SerializeSession</c>
/// does not exist in the shipped package). The serialized session carries the
/// <see cref="AgentSessionStateBag"/>, which is also where the default
/// in-memory chat history provider stores the conversation messages.
/// </summary>
public static class SessionPersistence
{
    private static readonly string Dir = "threads";

    public static async Task SaveAsync(ChatClientAgent agent, AgentSession session, string id)
    {
        Directory.CreateDirectory(Dir);
        var serialized = await agent.SerializeSessionAsync(session);
        await File.WriteAllTextAsync(Path.Combine(Dir, $"{id}.json"), serialized.GetRawText());
    }

    public static async Task<AgentSession> LoadAsync(ChatClientAgent agent, string id)
    {
        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(Dir, $"{id}.json")));
        return await agent.DeserializeSessionAsync(doc.RootElement);
    }
}

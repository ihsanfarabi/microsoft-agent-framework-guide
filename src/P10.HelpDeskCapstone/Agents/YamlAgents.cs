// yaml agents loading — verified against Microsoft.Agents.AI.Declarative
// 1.19.0-rc1 (decompiled): the factory classes and the
// CreateFromYamlAsync extension live in the `Microsoft.Agents.AI`
// namespace, not `.Declarative` as the doc claims; `using …Declarative`
// is only needed for types like FeatureIndex.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P10.HelpDeskCapstone.Agents;

/// <summary>
/// Loads declarative agents from <c>agents/*.yaml</c> via
/// <see cref="Microsoft.Agents.AI.Declarative.ChatClientPromptAgentFactory"/>
/// (a ChatClientAgent per YAML <c>kind: Prompt</c> definition) on the shared
/// Ollama chat client. Dictionary keyed by the agent's <c>name</c> field —
/// the key AddAIAgent and the keyed A2A registration must use.
/// </summary>
public static class YamlAgents
{
    public static async Task<Dictionary<string, AIAgent>> LoadAllAsync(string dir, IChatClient client)
    {
        var factory = new Microsoft.Agents.AI.ChatClientPromptAgentFactory(client);
        var agents = new Dictionary<string, AIAgent>();
        foreach (var path in Directory.GetFiles(dir, "*.yaml"))
        {
            var agent = await factory.CreateFromYamlAsync(await File.ReadAllTextAsync(path));
            agents[agent.Name!] = agent;
        }
        return agents;
    }
}
using System;
using System.Collections.Generic;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// The built-in agent definitions (Agent/Ask/Plan). Single source of truth for both the
/// tool window runtime and the settings Agents tab, where they can be overridden and
/// reverted to these defaults.
/// </summary>
internal static class BuiltinAgents
{
    public static readonly AgentConfig Agent = new AgentConfig
    {
        Name = "Agent",
        DisplayName = "Agent",
        SystemPrompt = "You are a capable coding agent. Use available tools to read, write, and execute code. Be concise in explanations but thorough in code changes.",
        IsBuiltin = true,
    };

    public static readonly AgentConfig Ask = new AgentConfig
    {
        Name = "Ask",
        DisplayName = "Ask",
        SystemPrompt = "You answer questions concisely. Do not use tools. Provide direct, focused answers.",
        Tools = Array.Empty<string>(),
        IsBuiltin = true,
    };

    public static readonly AgentConfig Plan = new AgentConfig
    {
        Name = "Plan",
        DisplayName = "Plan",
        SystemPrompt = "You are a senior software engineer producing structured implementation plans. Do NOT edit files or run commands; plan only. Respond in markdown with exactly these sections: ## Understanding (1-3 sentences restating the task), ## Assumptions (bullet list of decisions and scope boundaries), ## Approach (1-3 paragraphs with specific file/symbol references), ## Key Files (bullet list with one-line reasons), ## Risks and Open Questions (bullet list), ## Steps (numbered checklist, one verb + one target per step, with indented sub-bullets for breakdown). Be concrete: name real files, types, and endpoints. If the request is ambiguous, state assumptions explicitly.",
        IsBuiltin = true,
    };

    public static readonly IReadOnlyList<AgentConfig> All = new[] { Agent, Ask, Plan };
}

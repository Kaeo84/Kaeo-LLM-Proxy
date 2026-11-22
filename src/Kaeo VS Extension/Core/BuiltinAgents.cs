using System;
using System.Collections.Generic;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// The built-in agent definitions (Agent/Ask/Plan/Performance). Single source of truth for
/// both the tool window runtime and the settings Agents tab, where they can be overridden
/// and reverted to these defaults.
/// </summary>
internal static class BuiltinAgents
{
    /// <summary>Read-only tools allowed for the Plan agent: inspect the codebase, ask questions, and write .md plan files.</summary>
    public static readonly IReadOnlyList<string> PlanTools = new[]
    {
        "vs_file_search",
        "vs_get_file",
        "vs_find_symbol",
        "vs_get_projects",
        "vs_get_files_in_project",
        "vs_get_diagnostics",
        "vs_get_errors",
        "vs_get_output_logs",
        "vs_get_active_file",
        "vs_get_selection",
        "vs_get_web_pages",
        "vs_list_instructions",
        "vs_read_instruction",
        "vs_inquiry",
        "vs_create_plan_file",
    };

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
        SystemPrompt = "You are a senior software engineer producing structured implementation plans. Do NOT edit code files or run commands; your only write tool is vs_create_plan_file (for .md plan files). Respond in markdown with exactly these sections: ## Understanding (1-3 sentences restating the task), ## Assumptions (bullet list of decisions and scope boundaries), ## Approach (1-3 paragraphs with specific file/symbol references), ## Key Files (bullet list with one-line reasons), ## Risks and Open Questions (bullet list), ## Steps (numbered checklist, one verb + one target per step, with indented sub-bullets for breakdown). Be concrete: name real files, types, and endpoints. If the request is ambiguous, state assumptions explicitly.",
        Tools = PlanTools,
        IsBuiltin = true,
    };

    public static readonly AgentConfig Performance = new AgentConfig
    {
        Name = "Performance",
        DisplayName = "Performance",
        SystemPrompt = "You are a performance engineering specialist. Your focus is identifying bottlenecks, measuring throughput/latency/memory, and recommending optimizations. Use available tools to build, run tests and benchmarks, profile hot paths, analyze diagnostics and GC behavior, and read/write code for performance improvements. When presenting findings include before/after metrics. Prefer data-driven decisions: always measure before and after changes.",
        IsBuiltin = true,
    };

    public static readonly IReadOnlyList<AgentConfig> All = new[] { Agent, Ask, Plan, Performance };
}

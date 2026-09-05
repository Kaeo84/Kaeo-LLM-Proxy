# Plan: Implement Kaeo VS 2026 Extension (Copilot-style)

This file records the implementation steps performed by the automated agent.

## Completed work
- Scaffolding created for two new projects:
  - Kaeo LLM Proxy VS Extension.Core (net10.0 class library)
	- OllamaApiClient.cs
	- CopilotSdkBridge.cs
	- ExtensionSettingsStore.cs
	- ExtensionLogger.cs
	- McpServerManager.cs
	- ChatEngine.cs
  - Kaeo LLM Proxy VS Extension (VSIX placeholder)
	- source.extension.vsixmanifest
	- Commands.vsct
	- VsPackage.cs
	- ToolWindow control and pane
	- Settings modal WPF skeleton
- Host project file updated to exclude the new project directories from the default compile glob.

## Next steps for local developer
1. Verify the `GitHub.Copilot.SDK` package version and adjust references in the Core csproj as needed.
2. Build the solution in Visual Studio 2026, add the projects to the solution if desired.
3. Implement full Copilot SDK integration in `CopilotSdkBridge` and wire the UI to the ChatEngine.
4. Package the VSIX (build) and install into Visual Studio for verification.

## Notes
- AutoPilot is implemented as a custom strategy to be completed in `CopilotSdkBridge`.
- MCP stdio server lifecycle requires careful process management when implemented.


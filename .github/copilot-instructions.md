# Copilot Instructions

## Project Guidelines
- After every code change, always perform a git commit to the GitHub repo as the last step. The commit message should include a short summary of what the change was for and what was changed.
- For this unreleased/new app, do not add migration/update compatibility paths or unnecessary migration code unless explicitly requested.

## File/Database Access
- In Kaeo-LLM-Proxy, when multiple app instances may run concurrently (AllowMultipleInstances setting), ensure that file/database access code tolerates sharing violations gracefully. Catch `IOException`, log a warning, and degrade gracefully rather than allowing unhandled crashes.
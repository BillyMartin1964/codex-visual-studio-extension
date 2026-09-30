# Codex Community Connector

Run Codex inside Visual Studio without leaving the IDE.

`Codex Community Connector` adds a docked Codex tool window to Visual Studio 2022 and 2026. It uses your local Codex CLI installation, keeps the conversation inside Visual Studio, and stays compatible with the active Visual Studio theme and the extension's current UI languages.

This is an independent community project. It is not affiliated with, endorsed by, or officially associated with OpenAI or ChatGPT.

Author: [Rodrigo Jager](https://rodrigojager.com). [Source code](https://github.com/rodrigojager/codex-visual-studio-extension) · [Changelog](https://github.com/rodrigojager/codex-visual-studio-extension/blob/main/CHANGELOG.md).

## Highlights

- Docked chat-style Codex panel inside Visual Studio
- VS Code-style Codex header, composer controls, and settings access
- Independent settings tab in the Visual Studio document well, with immediate synchronization back to the chat
- Commands and context menus for opening Codex, starting a new agent, adding selection/file context, reviewing code, and implementing selected context
- Normal mode and plan mode
- Separate additional-information prompt window so the chat stays visible while answering plan questions
- Markdown output with a render/text toggle for easier reading and copy/paste
- Model selection
- Reasoning effort selection
- Verbosity selection
- `approval_policy` and `sandbox_mode` controls
- Manual model, reasoning, verbosity, and service-tier overrides through local settings
- Solution folder as working directory
- Protected, bounded local prompt history with an explicit clear action
- Bounded long-conversation hydration with explicit older-message batches and optional context compaction
- One-shot clipboard and file-based image attachments
- `@file` lookup against the current solution
- Session usage and rate-limit visibility in the Visual Studio UI
- VS Code-compatible composer defaults and local settings

## Authentication and Provider Support

Authentication belongs to the person running Visual Studio. The recommended setup is `codex login` with that user's own ChatGPT account; the extension reuses that local Codex CLI session.

Advanced user-local setups remain supported:

- `OPENAI_API_KEY` defined in that user's environment
- Provider-based configuration in `~/.codex/config.toml`
- Profile-based configuration that selects a provider from `config.toml`

The repository and VSIX contain no ChatGPT cookie, token, API key, `auth.json`, or publisher credential. Each installation uses only that Windows user's local Codex authentication, and the WebView is not given `OPENAI_API_KEY` from the Visual Studio process.

## Requirements

- Visual Studio 2022 or Visual Studio 2026, 64-bit
- Codex CLI installed locally
- A working local Codex authentication or provider configuration

## Notes

- The extension calls your local Codex CLI or app-server flow; it does not bundle the runtime.
- If your local Codex setup already works in the terminal, the extension is designed to reuse that setup.
- Image attachments are sent as `localImage` inputs through `codex app-server`; the selected local file must remain readable until the turn starts.
- Theme support uses Visual Studio theme resources so the UI works in light and dark themes.
- UI strings remain localized through the extension's existing localization pipeline.
- The extension is distributed as a standard VSIX package through the Visual Studio Marketplace.

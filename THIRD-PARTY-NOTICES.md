# Third-party notices

Codex Community Connector includes the following third-party component in its distributed VSIX.

## OpenAI Codex official webview

- Product: OpenAI Codex IDE extension (`OpenAI.chatgpt`)
- Frozen webview version: `26.5527.31454`
- Local synchronization source: `CyberVinci/Modificacoes/Codex/packages/codex/resources/webview`
- Bundled destination: `CodexVsix/UI/CodexWebview/webview`
- Upstream project: https://github.com/openai/codex

The webview bundle is kept as an unmodified frozen frontend artifact. It is
hosted by a Visual Studio-specific WebView2 bridge; no ownership or
relicensing of OpenAI artwork, translations, or frontend assets is implied.

## CyberVinci Codex host bridge reference

- Source: `CyberVinci/Modificacoes/Codex/packages/codex`
- Package: `@cybervinci/codex`
- License declared by the source package: EPL-2.0 OR GPL-2.0-only WITH
  Classpath-exception-2.0

The Visual Studio host is a C# adaptation of the CyberVinci/Theia bridge
contract, including the VS Code API shim transport, app-server message
routing, persisted state, and theme-variable mapping.

## Mermaid 11.15.0

- Project: https://github.com/mermaid-js/mermaid
- Package: `mermaid@11.15.0`
- Bundled file: `CodexVsix/UI/Assets/mermaid.min.js`
- Bundled file SHA-256: `70137E77BB273BB2EF972B86E8B0400CCA8BE53CB25BFC45911A186DC98665DE`
- npm package integrity: `sha512-pTMbcf3rWdtLiYGpmoTjHEpeY8seiy6sR+9nD7LOs8KfUbHE4lOUAprTRqRAcWSQ6MQpdX+YEsxShtGsINtPtw==`
- License: MIT

The MIT License (MIT)

Copyright (c) 2014 - 2022 Knut Sveidqvist

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

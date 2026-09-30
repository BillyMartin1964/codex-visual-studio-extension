using System;
using System.Collections.Generic;
using System.IO;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexOfficialWebViewShellTests
{
    [Fact]
    public void OfficialShellUsesWebView2TransportThemeLocaleAndFrozenAssets()
    {
        var resourceRoot = ResolveResourceRoot();
        Assert.True(File.Exists(Path.Combine(resourceRoot, "webview", "index.html")));
        Assert.True(File.Exists(Path.Combine(resourceRoot, "codex-acquire-vscode-api-shim.js")));
        Assert.True(File.Exists(Path.Combine(resourceRoot, "codex-visual-studio-history-guard.js")));
        Assert.True(File.Exists(Path.Combine(resourceRoot, "codex-visual-studio-diagnostics.js")));
        var theme = CodexVisualStudioTheme.Create(
            "test-dark",
            "dark",
            new Dictionary<string, string>
            {
                ["--vscode-foreground"] = "#f4f4f5",
                ["--vscode-sideBar-background"] = "#202020"
            });

        var html = CodexOfficialWebViewShell.Build(
            resourceRoot,
            "test-webview",
            "pt-BR",
            theme,
            "/settings",
            diagnosticLoggingEnabled: true);

        Assert.Contains("https://" + CodexOfficialWebViewShell.AssetHostName + "/webview/assets/", html);
        Assert.Contains("codex-assets-v" + ExtensionInfo.Version.Replace('.', '-') + ".local", html);
        Assert.DoesNotContain("https://codex-assets.local/", html);
        Assert.Contains("window.chrome.webview.postMessage", html);
        Assert.DoesNotContain("window.parent.postMessage({ channel: CHANNEL", html);
        Assert.Contains("name=\"codex-version\"", html);
        Assert.Contains("name=\"initial-route\" content=\"/settings\"", html);
        Assert.Contains("name=\"codex-diagnostic-logging-enabled\" content=\"true\"", html);
        Assert.Contains("<html lang=\"pt-BR\"", html);
        Assert.Contains("webviewId=test-webview", html);
        Assert.Contains("--vscode-sideBar-background:#202020", html);
        Assert.Contains("history-load-older", html);
        Assert.Contains("history-auto-compact-set", html);
        Assert.Contains("content-visibility: auto", html);
        Assert.Contains("codex-vs-history-window", html);
        Assert.DoesNotContain("codex-vs-back-to-chat", html);
        Assert.DoesNotContain("Voltar ao chat", html);
        Assert.Contains("codex-vs-recent-history", html);
        Assert.Contains("recent-history-request", html);
        Assert.Contains("Hist\\u00f3rico de tarefas", html);
        Assert.Contains("console-error:", html);
        Assert.Contains("diagnostic-settings-request", html);
        Assert.Contains("codex-vs-diagnostics-setting", html);
        Assert.Contains("normalized === '/settings/general-settings'", html);
        Assert.Contains("!diagnosticLoggingEnabled", html);
        Assert.DoesNotContain("PROD_BASE_TAG_HERE", html);
        Assert.DoesNotContain("PROD_CSP_TAG_HERE", html);
    }

    [Fact]
    public void DiagnosticLoggingIsOffByDefaultInTheGeneratedShell()
    {
        var resourceRoot = ResolveResourceRoot();
        var html = CodexOfficialWebViewShell.Build(
            resourceRoot,
            "diagnostics-default",
            "en-US",
            CodexVisualStudioTheme.Create("test", "dark", new Dictionary<string, string>()));

        Assert.Contains("name=\"codex-diagnostic-logging-enabled\" content=\"false\"", html);
    }

    [Fact]
    public void ShimAdaptationRejectsAnUnknownTransportShape()
    {
        Assert.Throws<InvalidDataException>(() =>
            CodexOfficialWebViewShell.AdaptShimForWebView2("window.parent.postMessage('different');"));
    }

    private static string ResolveResourceRoot()
    {
        var outputRoot = Path.Combine(AppContext.BaseDirectory, "UI", "CodexWebview");
        if (File.Exists(Path.Combine(outputRoot, "webview", "index.html")))
        {
            return outputRoot;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "CodexVsix",
            "UI",
            "CodexWebview"));
    }
}

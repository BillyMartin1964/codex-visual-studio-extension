using System;
using System.IO;
using System.Linq;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexOfficialWebViewBridgeTests
{
    [Fact]
    public void UsesCyberVinciNeutralFallbacksForChatGptWebEndpoints()
    {
        var tasks = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://chatgpt.com/wham/tasks/list?limit=20")));
        Assert.Empty(Assert.IsType<JArray>(tasks["items"]));
        Assert.Equal(JTokenType.Null, tasks["cursor"]?.Type);

        var statsig = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://chatgpt.com/ces/v1/rgstr?secret-is-not-forwarded")));
        Assert.False(statsig["has_updates"]?.Value<bool>());
        Assert.True(statsig["time"]?.Value<long>() > 0);
    }

    [Theory]
    [InlineData("https://chatgpt.com/wham/tasks/list/")]
    [InlineData("https://chatgpt.com/backend-api/wham/tasks/list?limit=20&task_filter=current")]
    public void TaskHistoryFallbackAcceptsSupportedChatGptUrlVariants(string url)
    {
        var tasks = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(new Uri(url)));

        Assert.Empty(Assert.IsType<JArray>(tasks["items"]));
        Assert.Equal(JTokenType.Null, tasks["cursor"]?.Type);
    }

    [Fact]
    public void RelativeFetchUrlsResolveAgainstChatGptWithoutAcceptingLocalFileSchemes()
    {
        var resolved = CodexOfficialWebViewBridge.ResolveProxyUri("/wham/tasks/list?limit=20");

        Assert.Equal("https", resolved.Scheme);
        Assert.Equal("chatgpt.com", resolved.Host);
        Assert.Equal("/wham/tasks/list", resolved.AbsolutePath);
        Assert.Throws<InvalidOperationException>(() => CodexOfficialWebViewBridge.ResolveProxyUri("file:///C:/secrets.txt"));
    }

    [Theory]
    [InlineData("navigate-back")]
    [InlineData("navigate-forward")]
    public void HistoryNavigationMessagesAreRelayedToTheOfficialRouter(string type)
    {
        var message = CodexOfficialWebViewBridge.CreateHistoryNavigationMessage(type);

        Assert.Equal(type, message["type"]?.Value<string>());
    }

    [Theory]
    [InlineData("/settings", true)]
    [InlineData("/settings/general-settings", true)]
    [InlineData("/settings/agent?source=profile", true)]
    [InlineData("/settings-not-a-route", false)]
    [InlineData("/local/thread-id", false)]
    public void SettingsRoutesAreDetectedWithoutCapturingChatRoutes(string route, bool expected)
    {
        Assert.Equal(expected, CodexOfficialWebViewBridge.IsSettingsRoute(route));
    }

    [Theory]
    [InlineData("/settings", "")]
    [InlineData("/settings/general-settings", "general-settings")]
    [InlineData("/settings/keyboard-shortcuts?source=menu", "keyboard-shortcuts")]
    public void SettingsSectionIsResolvedForTheIndependentSurface(string route, string expected)
    {
        var section = CodexOfficialWebViewBridge.ResolveSettingsSection(
            new JObject { ["path"] = route });

        Assert.Equal(expected, section);
    }

    [Theory]
    [InlineData("reviewDelivery", "reviewDelivery")]
    [InlineData("chatgpt.reviewDelivery", "reviewDelivery")]
    [InlineData(" chatgpt.localeOverride ", "localeOverride")]
    [InlineData("show-context-window-usage", "show-context-window-usage")]
    public void OfficialAndLegacySettingKeysUseTheSameCanonicalName(string key, string expected)
    {
        Assert.Equal(expected, CodexOfficialWebViewBridge.NormalizeSettingKey(key));
    }

    [Fact]
    public void SettingsSnapshotExposesOfficialAndLegacyAliasesWithoutLettingStaleStateWin()
    {
        var settings = new CodexVsix.Models.CodexExtensionSettings
        {
            LanguageOverride = "pt-BR",
            FollowUpQueueMode = "steer",
            ComposerEnterBehavior = "cmdIfMultiline",
            ReviewDelivery = "detached",
            EnableDiagnosticLogging = true
        };
        var persistedState = new JObject
        {
            ["setting:reviewDelivery"] = "inline",
            ["setting:show-context-window-usage"] = true,
            ["setting:chatgpt.preventSleepWhileRunning"] = true,
            ["unrelated"] = "must-not-be-exposed"
        };

        var values = CodexOfficialWebViewBridge.BuildSettingsValues(settings, persistedState);

        Assert.Equal("detached", values["reviewDelivery"]?.Value<string>());
        Assert.Equal("detached", values["chatgpt.reviewDelivery"]?.Value<string>());
        Assert.Equal("steer", values["followUpQueueMode"]?.Value<string>());
        Assert.Equal("cmdIfMultiline", values["composerEnterBehavior"]?.Value<string>());
        Assert.Equal("pt-BR", values["localeOverride"]?.Value<string>());
        Assert.True(values["diagnosticLoggingEnabled"]?.Value<bool>());
        Assert.True(values["chatgpt.diagnosticLoggingEnabled"]?.Value<bool>());
        Assert.True(values["show-context-window-usage"]?.Value<bool>());
        Assert.True(values["chatgpt.show-context-window-usage"]?.Value<bool>());
        Assert.True(values["preventSleepWhileRunning"]?.Value<bool>());
        Assert.Null(values["unrelated"]);
    }

    [Fact]
    public void OfficialSettingsRefreshDoesNotInvalidateEveryViewModelBinding()
    {
        var viewModelSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "ViewModels",
            "CodexToolWindowViewModel.cs"));
        var methodStart = viewModelSource.IndexOf(
            "internal void NotifySettingsChangedFromOfficialWebView(string settingKey)",
            StringComparison.Ordinal);
        var nextMethod = viewModelSource.IndexOf(
            "private void ApplySettings()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var methodSource = viewModelSource.Substring(methodStart, nextMethod - methodStart);
        Assert.DoesNotContain("OnPropertyChanged(string.Empty)", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(SelectedReviewDelivery))", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(SelectedFollowUpQueueMode))", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(DiagnosticLoggingEnabled))", methodSource);
    }

    [Fact]
    public void QueryInvalidationBroadcastAlwaysIncludesThePrimaryChatHost()
    {
        var hostSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "UI",
            "CodexOfficialWebViewHost.cs"));
        var methodStart = hostSource.IndexOf(
            "public static void BroadcastQueryInvalidation",
            StringComparison.Ordinal);
        var nextMethod = hostSource.IndexOf(
            "public static bool TryPrefillComposer",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var methodSource = hostSource.Substring(methodStart, nextMethod - methodStart);
        Assert.Contains("_primary.TryGetTarget(out var primary)", methodSource);
        Assert.Contains("targets.Add(primary)", methodSource);
    }

    [Fact]
    public void QueryInvalidationBroadcastIsBoundedAndCloned()
    {
        var original = new JArray(
            "host-rpc",
            "get-settings",
            new JObject { ["scope"] = "local" });

        var normalized = CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(original);

        Assert.NotNull(normalized);
        Assert.NotSame(original, normalized);
        normalized![0] = "changed";
        Assert.Equal("host-rpc", original[0]?.Value<string>());

        var notification = CodexOfficialWebViewBridge.CreateQueryInvalidationNotification(original);
        Assert.Equal("mcp-notification", notification["type"]?.Value<string>());
        Assert.Equal("query-cache-invalidate", notification["method"]?.Value<string>());
        Assert.Equal("host-rpc", notification["params"]?["queryKey"]?[0]?.Value<string>());
        Assert.Null(CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(new JArray()));
        Assert.Null(CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(
            new JArray(Enumerable.Range(0, 33))));
    }

    [Fact]
    public void RecentConversationRefreshUsesABoundedThreadListRequest()
    {
        var parameters = CodexOfficialWebViewBridge.BuildRecentConversationRefreshParams(
            new JObject { ["sortKey"] = "created_at" });

        Assert.False(parameters["archived"]?.Value<bool>());
        Assert.Equal(50, parameters["limit"]?.Value<int>());
        Assert.Equal("created_at", parameters["sortKey"]?.Value<string>());
        Assert.Equal(JTokenType.Null, parameters["cursor"]?.Type);
        Assert.Equal(JTokenType.Null, parameters["modelProviders"]?.Type);
    }

    [Fact]
    public void RecentHistoryResponseIsBoundedAndExposesOnlySafeRowMetadata()
    {
        var threads = new JArray();
        for (var index = 0; index < 52; index++)
        {
            threads.Add(new JObject
            {
                ["id"] = "thread-" + index,
                ["name"] = index == 1 ? JValue.CreateNull() : "Task " + index,
                ["preview"] = "Preview " + index,
                ["cwd"] = Path.Combine("C:\\", "work", "project-" + index),
                ["updatedAt"] = 1_750_000_000L + index,
                ["turns"] = new JArray(new JObject { ["text"] = "must-not-cross-the-bridge" }),
                ["path"] = "C:\\private\\thread.jsonl"
            });
        }

        var response = CodexOfficialWebViewBridge.BuildRecentHistoryResponse(new JObject
        {
            ["data"] = threads,
            ["nextCursor"] = "more"
        });
        var items = Assert.IsType<JArray>(response["items"]);
        var first = Assert.IsType<JObject>(items[0]);
        var second = Assert.IsType<JObject>(items[1]);

        Assert.Equal(50, items.Count);
        Assert.True(response["hasMore"]?.Value<bool>());
        Assert.Equal("thread-0", first["id"]?.Value<string>());
        Assert.Equal("Task 0", first["title"]?.Value<string>());
        Assert.Equal("project-0", first["workspaceName"]?.Value<string>());
        Assert.Equal("Preview 1", second["title"]?.Value<string>());
        Assert.Null(first["cwd"]);
        Assert.Null(first["turns"]);
        Assert.Null(first["path"]);
        Assert.DoesNotContain("must-not-cross-the-bridge", response.ToString());
        Assert.DoesNotContain("C:\\private", response.ToString());
    }

    [Fact]
    public void WebViewNeverReceivesAnApiKeyFromTheVisualStudioProcess()
    {
        var response = CodexOfficialWebViewBridge.BuildOpenAiApiKeyResponse();

        Assert.Equal(JTokenType.Null, response["value"]?.Type);
    }

    [Fact]
    public void ConvertsAppServerRateLimitsToTheUsageShapeExpectedByTheWebView()
    {
        var response = JObject.Parse(
            "{\"rateLimits\":{\"planType\":\"plus\",\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1700000000}," +
            "\"secondary\":{\"usedPercent\":40,\"windowDurationMins\":10080,\"resetsAt\":1700500000}}}");

        var usage = CodexOfficialWebViewBridge.ConvertRateLimitsToUsage(response);

        Assert.NotNull(usage);
        Assert.Equal("plus", usage!["plan_type"]?.Value<string>());
        Assert.Equal(25, usage["rate_limit"]?["primary_window"]?["used_percent"]?.Value<double>());
        Assert.Equal(18000, usage["rate_limit"]?["primary_window"]?["limit_window_seconds"]?.Value<double>());
        Assert.Equal(1700500000, usage["rate_limit"]?["secondary_window"]?["reset_at"]?.Value<long>());
        Assert.Null(CodexOfficialWebViewBridge.ConvertRateLimitsToUsage(new JObject()));
    }

    [Fact]
    public void LeavesUnrelatedNetworkRequestsForTheProxy()
    {
        Assert.Null(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://example.com/data.json")));
    }

    [Fact]
    public void IdeContextUsesTheObjectShapeExpectedByTheOfficialWebView()
    {
        var root = Path.Combine("C:\\", "work", "sample");
        var activePath = Path.Combine(root, "Controllers", "PlansController.cs");
        var otherPath = Path.Combine(root, "Services", "PlanService.cs");

        var response = CodexWebViewIdeContext.BuildResponse(
            root,
            activePath,
            "var plan = await repo.GetPlanAsync();",
            new[] { activePath, otherPath });

        var ideContext = Assert.IsType<JObject>(response["ideContext"]);
        var activeFile = Assert.IsType<JObject>(ideContext["activeFile"]);
        Assert.Equal(activePath, activeFile["path"]?.Value<string>());
        Assert.Equal("var plan = await repo.GetPlanAsync();", activeFile["activeSelectionContent"]?.Value<string>());

        var openTabs = Assert.IsType<JArray>(ideContext["openTabs"]);
        Assert.Equal(2, openTabs.Count);
        Assert.Equal("PlansController.cs", openTabs[0]?["label"]?.Value<string>());
        Assert.Equal(otherPath, openTabs[1]?["path"]?.Value<string>());
    }

    [Fact]
    public void IdeContextSafelyRepresentsTheAbsenceOfAnActiveDocument()
    {
        var response = CodexWebViewIdeContext.BuildResponse(
            "C:\\work",
            null,
            "selection without a document",
            Array.Empty<string>());

        var ideContext = Assert.IsType<JObject>(response["ideContext"]);
        Assert.Null(ideContext["activeFile"]);
        Assert.Empty(Assert.IsType<JArray>(ideContext["openTabs"]));
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateParts = new[] { directory.FullName }.Concat(parts).ToArray();
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }
}

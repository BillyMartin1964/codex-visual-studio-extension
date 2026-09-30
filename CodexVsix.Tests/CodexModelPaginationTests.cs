using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexModelPaginationTests
{
    [Fact]
    public async Task AllPagesPreserveCapabilitiesAndAnnouncementMetadata()
    {
        var requests = new List<JObject>();
        var original = new JObject { ["includeHidden"] = true, ["limit"] = 20, ["cursor"] = JValue.CreateNull() };
        var model = JObject.Parse(@"{'model':'gpt-6.1-sol','displayName':'GPT-6.1 Sol',
          'hidden':false,'isDefault':true,'defaultReasoningEffort':'low',
          'supportedReasoningEfforts':[{'reasoningEffort':'ultra','description':'runtime description'}],
          'availabilityNux':{'title':'New model'},'upgrade':'gpt-6.1-sol','inputModalities':['text','image']}");
        var result = await CodexModelCatalog.ReadAllPagesAsync((values, _) =>
        {
            requests.Add((JObject)values.DeepClone());
            return Task.FromResult<JToken?>(new JObject
            {
                ["data"] = requests.Count == 1 ? new JArray(new JObject { ["model"] = "first" }) : new JArray(model),
                ["nextCursor"] = requests.Count == 1 ? new JValue("second") : JValue.CreateNull()
            });
        }, original, CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => { Assert.True(request["includeHidden"]?.Value<bool>()); Assert.Equal(20, request["limit"]?.Value<int>()); });
        Assert.Equal("second", requests[1]["cursor"]?.Value<string>());
        Assert.Equal(JTokenType.Null, original["cursor"]?.Type);
        var data = Assert.IsType<JArray>(result?["data"]);
        Assert.True(JToken.DeepEquals(model, data[1]));
        Assert.Equal(JTokenType.Null, result?["nextCursor"]?.Type);
    }

    [Fact]
    public async Task ExplicitCursorRetainsTheSinglePageContract()
    {
        var page = new JObject { ["data"] = new JArray(), ["nextCursor"] = "next" };
        var count = 0;
        var result = await CodexModelCatalog.ReadAllPagesAsync((_, _) => { count++; return Task.FromResult<JToken?>(page); },
            new JObject { ["cursor"] = "requested" }, CancellationToken.None);
        Assert.Same(page, result);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RepeatedCursorFailsInsteadOfPublishingAnIncompleteCatalog()
    {
        var count = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexModelCatalog.ReadAllPagesAsync((_, _) =>
        {
            count++;
            return Task.FromResult<JToken?>(new JObject { ["data"] = new JArray(), ["nextCursor"] = "same" });
        }, null, CancellationToken.None));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CancellationDoesNotReadAnotherPage()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CodexModelCatalog.ReadAllPagesAsync((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<JToken?>(new JObject { ["data"] = new JArray(), ["nextCursor"] = "more" });
        }, null, cancellation.Token));
    }

    [Fact]
    public void NewFallbackModelsKeepExistingChoicesAndModelSpecificEfforts()
    {
        var models = CodexModelCatalog.CreateFallbackOptions();
        foreach (var name in new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.5" })
            Assert.Single(models, model => model.Value == name);
        Assert.Contains("ultra", models.Single(model => model.Value == "gpt-6.1-sol").SupportedReasoningEfforts);
        Assert.DoesNotContain("ultra", models.Single(model => model.Value == "gpt-6-luna").SupportedReasoningEfforts);
        Assert.Equal("medium", CodexModelCatalog.ResolveReasoningEffort(models.Single(model => model.Value == "gpt-6-luna"), "ultra"));
    }

    [Fact]
    public void AlternateCatalogShapeAndInvalidEntriesDoNotBreakParsing()
    {
        var response = JObject.Parse(@"{'models':[null,42,{'model':'gpt-6-astra','hidden':false}]}");
        Assert.Equal("gpt-6-astra", Assert.Single(CodexModelCatalog.ParseModelListResponse(response, false)).Value);
        Assert.Empty(CodexModelCatalog.ParseModelListResponse(new JValue("invalid"), false));
    }
}

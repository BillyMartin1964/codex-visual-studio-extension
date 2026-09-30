using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderAuthResolverTests
{
    private static JObject CustomConfig => JObject.Parse(@"{
      'config': {'model_provider':'proxy', 'model_providers':{'proxy':{'base_url':'https://example.invalid/v1'}}}
    }");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitRuntimeDecisionIsNeverOverridden(bool requiresAuth)
    {
        var account = new JObject { ["account"] = JValue.CreateNull(), ["requiresOpenaiAuth"] = requiresAuth };
        var result = await CodexProviderAuthResolver.ReadAccountAsync((method, _, _) =>
        {
            Assert.Equal("account/read", method);
            return Task.FromResult<JToken?>(account);
        }, null, "company", "C:\\work", CancellationToken.None);
        Assert.Same(account, result);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The test controls an asynchronous completion source to verify that the resolver waits for config/read.")]
    public async Task FailedAccountReadWaitsForEffectiveConfigAndKeepsWorkingDirectory()
    {
        var config = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CodexProviderAuthResolver.ReadAccountAsync((method, parameters, _) =>
        {
            if (method == "account/read") throw new InvalidOperationException("probe failed");
            Assert.Equal("config/read", method);
            Assert.Equal("C:\\work", parameters?["cwd"]?.Value<string>());
            Assert.False(parameters?["includeLayers"]?.Value<bool>());
            return config.Task;
        }, null, null, "C:\\work", CancellationToken.None);
        Assert.False(pending.IsCompleted);
        config.SetResult(CustomConfig);
        var result = await pending;
        Assert.False(result?["requiresOpenaiAuth"]?.Value<bool>());
        Assert.Equal(JTokenType.Null, result?["account"]?.Type);
    }

    [Theory]
    [InlineData("company")]
    [InlineData(null)]
    public void SelectedProfileOverridesTheRootProvider(string? selectedProfile)
    {
        var config = CustomConfig;
        config["config"]!["model_provider"] = "openai";
        config["config"]!["profile"] = "company";
        config["config"]!["profiles"] = JObject.Parse(@"{'company':{'model_provider':'proxy'}}");
        Assert.NotNull(CodexProviderAuthResolver.TryCreateProviderAccount(config, selectedProfile));
        Assert.Equal("openai", config["config"]?["model_provider"]?.Value<string>());
        Assert.Null(CodexProviderAuthResolver.TryCreateProviderAccount(config, "missing"));
    }

    [Fact]
    public void CustomProviderRequiringOpenAiAuthKeepsTheLoginGate()
    {
        var config = CustomConfig;
        config["config"]!["model_providers"]!["proxy"]!["requires_openai_auth"] = true;
        Assert.Null(CodexProviderAuthResolver.TryCreateProviderAccount(config, null));
        config["config"]!["model_providers"]!["proxy"]!["requires_openai_auth"] = false;
        Assert.NotNull(CodexProviderAuthResolver.TryCreateProviderAccount(config, null));
        config["config"]!["model_provider"] = "openai";
        Assert.Null(CodexProviderAuthResolver.TryCreateProviderAccount(config, null));
    }

    [Fact]
    public async Task ConfigFailureKeepsTheOriginalErrorAndCancellationDoesNotFallback()
    {
        var original = new InvalidOperationException("original");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexProviderAuthResolver.ReadAccountAsync((method, _, _) =>
                throw (method == "account/read" ? original : new Exception("config")),
                null, null, null, CancellationToken.None));
        Assert.Same(original, error);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CodexProviderAuthResolver.ReadAccountAsync((method, _, _) =>
            {
                Assert.Equal("account/read", method);
                throw new OperationCanceledException();
            }, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task AmbiguousAccountCanFallbackButUnknownProviderCannot()
    {
        var account = new JObject { ["account"] = JValue.CreateNull() };
        var result = await CodexProviderAuthResolver.ReadAccountAsync((method, _, _) =>
            Task.FromResult<JToken?>(method == "account/read" ? account : CustomConfig),
            null, null, null, CancellationToken.None);
        Assert.False(result?["requiresOpenaiAuth"]?.Value<bool>());
        var missing = new JObject { ["config"] = new JObject { ["model_provider"] = "unknown" } };
        Assert.Null(CodexProviderAuthResolver.TryCreateProviderAccount(missing, null));
    }

    [Fact]
    public async Task CompatibilityFallbackPreservesExistingAccountMetadata()
    {
        var account = JObject.Parse(@"{'account':{'type':'chatgpt','email':'test@example.invalid','planType':'plus'},'extra':'retained'}");
        var result = await CodexProviderAuthResolver.ReadAccountAsync((method, _, _) =>
            Task.FromResult<JToken?>(method == "account/read" ? account : CustomConfig),
            null, null, null, CancellationToken.None);
        Assert.Equal("test@example.invalid", result?["account"]?["email"]?.Value<string>());
        Assert.Equal("retained", result?["extra"]?.Value<string>());
        Assert.Null(account["requiresOpenaiAuth"]);
    }

    [Fact]
    public void ProfileOverridesAndProviderDefinitionsAcceptBothRuntimeNamingConventions()
    {
        var config = JObject.Parse(@"{'config':{'model_provider':'openai','profile':'company',
          'profiles':{'company':{'modelProvider':'proxy'}},'modelProviders':{'proxy':{'requiresOpenaiAuth':false}}}}");
        Assert.NotNull(CodexProviderAuthResolver.TryCreateProviderAccount(config, null));
        config["config"]!["modelProviders"]!["proxy"]!["requiresOpenaiAuth"] = true;
        Assert.Null(CodexProviderAuthResolver.TryCreateProviderAccount(config, null));
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal static class CodexProviderAuthResolver
{
    // An explicit account/read decision always wins, including for custom providers.
    internal static async Task<JToken?> ReadAccountAsync(
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendRequest,
        JToken? parameters,
        string? selectedProfile,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        JToken? account;
        Exception? accountError = null;
        try
        {
            account = await sendRequest("account/read", parameters, cancellationToken).ConfigureAwait(false);
            if (account is JObject record && record["requiresOpenaiAuth"]?.Type == JTokenType.Boolean)
            {
                return account;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            account = null;
            accountError = ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var config = await sendRequest("config/read", new JObject
            {
                ["includeLayers"] = false,
                ["cwd"] = string.IsNullOrWhiteSpace(workingDirectory) ? JValue.CreateNull() : new JValue(workingDirectory)
            }, cancellationToken).ConfigureAwait(false);
            if (TryCreateProviderAccount(config, selectedProfile) is JObject fallback)
            {
                if (account is JObject original)
                {
                    var resolved = (JObject)original.DeepClone();
                    resolved["requiresOpenaiAuth"] = false;
                    resolved["account"] ??= JValue.CreateNull();
                    return resolved;
                }

                return fallback;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep the original account result/error when the provider cannot be established.
        }

        if (accountError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(accountError).Throw();
        }

        return account;
    }

    internal static JObject? TryCreateProviderAccount(JToken? response, string? selectedProfile)
    {
        if (response is not JObject result || result["config"] is not JObject config)
        {
            return null;
        }

        var effective = (JObject)config.DeepClone();
        var profile = !string.IsNullOrWhiteSpace(selectedProfile)
            ? selectedProfile
            : ReadString(config["profile"]);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            if (config["profiles"] is not JObject profiles || profiles[profile!] is not JObject overrides)
            {
                return null;
            }

            foreach (var property in overrides.Properties())
            {
                if (property.Value.Type != JTokenType.Null)
                {
                    effective[property.Name] = property.Value.DeepClone();
                    // A profile override wins even if a legacy runtime uses the
                    // other naming convention for the root-level provider.
                    if (property.Name == "model_provider") effective.Remove("modelProvider");
                    if (property.Name == "modelProvider") effective.Remove("model_provider");
                }
            }
        }

        var provider = (ReadString(effective["model_provider"]) ?? ReadString(effective["modelProvider"]))?.Trim();
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var providers = effective["model_providers"] as JObject ?? effective["modelProviders"] as JObject;
        if (providers?[provider!] is not JObject definition)
        {
            return null;
        }

        var requiresAuth = definition["requires_openai_auth"] ?? definition["requiresOpenaiAuth"];
        if (requiresAuth is not null && requiresAuth.Type != JTokenType.Null
            && (requiresAuth.Type != JTokenType.Boolean || requiresAuth.Value<bool>()))
        {
            return null;
        }

        return new JObject { ["account"] = JValue.CreateNull(), ["requiresOpenaiAuth"] = false };
    }

    private static string? ReadString(JToken? value)
        => value?.Type == JTokenType.String ? value.Value<string>() : null;
}

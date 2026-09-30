using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal static class CodexModelCatalog
{
    private static readonly string[] AllReasoningEfforts =
    {
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
        "ultra"
    };

    internal static IReadOnlyList<string> GenericReasoningEfforts => AllReasoningEfforts;

    internal static async Task<JToken?> ReadAllPagesAsync(
        Func<JObject, CancellationToken, Task<JToken?>> readPage,
        JToken? parameters,
        CancellationToken cancellationToken)
    {
        var request = parameters is JObject values ? (JObject)values.DeepClone() : new JObject();
        // Explicit cursor requests retain their pagination contract.
        if (request["cursor"]?.Type == JTokenType.String)
        {
            return await readPage(request, cancellationToken).ConfigureAwait(false);
        }

        request["limit"] ??= 100;
        var data = new JArray();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        JObject? combined = null;
        for (var page = 0; page < 100; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await readPage(request, cancellationToken).ConfigureAwait(false);
            if (response is not JObject record || (record["data"] as JArray ?? record["models"] as JArray) is not JArray items)
            {
                throw new InvalidOperationException("The Codex model catalog has an invalid response shape.");
            }

            combined ??= (JObject)record.DeepClone();
            foreach (var item in items)
            {
                data.Add(item.DeepClone());
            }

            var cursor = record["nextCursor"]?.Value<string>();
            if (string.IsNullOrEmpty(cursor))
            {
                combined["data"] = data;
                if (combined["models"] is not null) combined["models"] = data.DeepClone();
                combined["nextCursor"] = JValue.CreateNull();
                return combined;
            }

            if (!cursors.Add(cursor!))
            {
                throw new InvalidOperationException("The Codex model catalog repeated a pagination cursor.");
            }

            request["cursor"] = cursor;
        }

        throw new InvalidOperationException("The Codex model catalog exceeded the pagination limit.");
    }

    internal static IReadOnlyList<CodexModelOption> ParseModelListResponse(JToken? response, bool includeHidden)
    {
        var models = new List<CodexModelOption>();
        if (response is not JObject record || (record["data"] as JArray ?? record["models"] as JArray) is not JArray items)
        {
            return models;
        }

        foreach (var item in items.OfType<JObject>())
        {
            if (!includeHidden && item?["hidden"]?.Value<bool>() == true)
            {
                continue;
            }

            var value = item?["model"]?.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var label = item?["displayName"]?.Value<string>()?.Trim();
            var defaultReasoningEffort = NormalizeReasoningEffort(item?["defaultReasoningEffort"]?.Value<string>());
            var supportedReasoningEfforts = (item?["supportedReasoningEfforts"] as JArray)?
                .Select(ReadReasoningEffort)
                .Where(effort => !string.IsNullOrWhiteSpace(effort))
                .Select(effort => effort!)
                ?? Enumerable.Empty<string>();

            models.Add(new CodexModelOption(
                string.IsNullOrWhiteSpace(label) ? value! : label!,
                value!,
                defaultReasoningEffort,
                supportedReasoningEfforts));
        }

        return models;
    }

    internal static IReadOnlyList<CodexModelOption> CreateFallbackOptions()
    {
        return new[]
        {
            Create("GPT-6.1 Sol", "gpt-6.1-sol", "low", "low", "medium", "high", "xhigh", "max", "ultra"),
            Create("GPT-6 Astra", "gpt-6-astra", "medium", "low", "medium", "high", "xhigh", "max", "ultra"),
            Create("GPT-6 Sol", "gpt-6-sol", "medium", "low", "medium", "high", "xhigh", "max", "ultra"),
            Create("GPT-6 Luna", "gpt-6-luna", "medium", "low", "medium", "high", "xhigh", "max"),
            Create("GPT-5.6 Sol", "gpt-5.6-sol", "low", "low", "medium", "high", "xhigh", "max", "ultra"),
            Create("GPT-5.6 Terra", "gpt-5.6-terra", "medium", "low", "medium", "high", "xhigh", "max", "ultra"),
            Create("GPT-5.6 Luna", "gpt-5.6-luna", "medium", "low", "medium", "high", "xhigh", "max"),
            Create("GPT-5.5", "gpt-5.5", "medium", "low", "medium", "high", "xhigh"),
            Create("GPT-5.4", "gpt-5.4", "medium", "low", "medium", "high", "xhigh"),
            Create("GPT-5.4 Mini", "gpt-5.4-mini", "medium", "low", "medium", "high", "xhigh"),
            Create("GPT-5.2", "gpt-5.2", "medium", "low", "medium", "high", "xhigh")
        };
    }

    internal static string NormalizeReasoningEffort(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "minimum":
            case "min":
                return "minimal";
            case "extra-high":
            case "extra high":
            case "x-high":
                return "xhigh";
            case "maximum":
                return "max";
            default:
                return normalized;
        }
    }

    internal static string ResolveReasoningEffort(
        CodexModelOption? model,
        string? requestedEffort,
        string fallbackEffort = "high")
    {
        var requested = NormalizeReasoningEffort(requestedEffort);
        if (model is null || !model.HasReasoningEffortMetadata)
        {
            return string.IsNullOrWhiteSpace(requested)
                ? NormalizeReasoningEffort(fallbackEffort)
                : requested;
        }

        var supported = model.SupportedReasoningEfforts;
        var match = FindSupportedEffort(supported, requested);
        if (!string.IsNullOrWhiteSpace(match))
        {
            return match!;
        }

        match = FindSupportedEffort(supported, model.DefaultReasoningEffort);
        if (!string.IsNullOrWhiteSpace(match))
        {
            return match!;
        }

        match = FindSupportedEffort(supported, NormalizeReasoningEffort(fallbackEffort));
        return !string.IsNullOrWhiteSpace(match) ? match! : supported[0];
    }

    private static CodexModelOption Create(
        string label,
        string value,
        string defaultReasoningEffort,
        params string[] supportedReasoningEfforts)
    {
        return new CodexModelOption(label, value, defaultReasoningEffort, supportedReasoningEfforts);
    }

    private static string? ReadReasoningEffort(JToken? option)
    {
        if (option?.Type == JTokenType.String)
        {
            return NormalizeReasoningEffort(option.Value<string>());
        }

        return option is JObject objectOption
            ? NormalizeReasoningEffort(objectOption["reasoningEffort"]?.Value<string>())
            : null;
    }

    private static string? FindSupportedEffort(IEnumerable<string> supportedEfforts, string? effort)
    {
        var normalized = NormalizeReasoningEffort(effort);
        return supportedEfforts.FirstOrDefault(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
    }
}

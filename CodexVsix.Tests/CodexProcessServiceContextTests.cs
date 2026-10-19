using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProcessServiceContextTests
{
    [Theory]
    [InlineData("oi")]
    [InlineData("qual modelo é você?")]
    public void PromptWithoutIdeContextRemainsTheOnlyTextRequest(string prompt)
    {
        var rawPrompt = CodexProcessService.BuildPromptText(
            prompt,
            ideContextSummary: string.Empty,
            preferredMcpContext: string.Empty);

        Assert.Equal(prompt, rawPrompt);
        Assert.Equal(prompt, CodexProcessService.ExtractPromptRequest(rawPrompt));
    }

    [Theory]
    [InlineData("oi")]
    [InlineData("qual modelo é você?")]
    public void IdeContextNeverBecomesPartOfTheOfficialUserText(string prompt)
    {
        const string ideContext = "## Solution: C:/work/Archdraw"
            + "\r\n\r\n## Active file: apps/api-dotnet/Data/DatabaseBootstrap.cs";

        var rawPrompt = CodexProcessService.BuildPromptText(
            prompt,
            ideContext,
            preferredMcpContext: string.Empty);

        Assert.Equal(prompt, rawPrompt);
        Assert.Equal(prompt, CodexProcessService.ExtractPromptRequest(rawPrompt));
        Assert.Equal(prompt, CodexProcessService.NormalizeUserMessageTextSegment(rawPrompt));
    }

    [Fact]
    public void HistoryDropsAnIdeContextOnlySyntheticEntry()
    {
        var contextOnly = CodexProcessService.IdeContextHeading
            + Environment.NewLine
            + "## Active file: src/Program.cs";

        Assert.Equal(string.Empty, CodexProcessService.NormalizeUserMessageTextSegment(contextOnly));
    }

    [Theory]
    [InlineData("oi")]
    [InlineData("qual modelo é você?")]
    public void BuildUserInputKeepsAutomaticIdeAndMcpContextOutOfTheUserText(string prompt)
    {
        using var temp = new TemporaryDirectory();
        var cwd = Path.Combine(temp.Path, "extension-project");
        Directory.CreateDirectory(cwd);
        var settings = new CodexExtensionSettings
        {
            LanguageOverride = "pt-BR",
            PreferredMcpServers = new List<string> { "github" }
        };
        using var service = new CodexProcessService();

        var inputs = JArray.FromObject(service.BuildUserInput(
            prompt,
            settings,
            cwd,
            Array.Empty<string>(),
            "## Solution: C:/work/Archdraw"));
        var textItems = inputs
            .OfType<JObject>()
            .Where(item => string.Equals(item["type"]?.Value<string>(), "text", StringComparison.Ordinal))
            .ToList();

        var textItem = Assert.Single(textItems);
        var rawPrompt = textItem["text"]?.Value<string>() ?? string.Empty;
        Assert.Equal(prompt, rawPrompt);
        Assert.DoesNotContain(CodexProcessService.IdeContextHeading, rawPrompt);
        Assert.DoesNotContain("## Solution: C:/work/Archdraw", rawPrompt);
        Assert.DoesNotContain("github", rawPrompt);
        Assert.False(rawPrompt.IndexOf(Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Equal(prompt, CodexProcessService.ExtractPromptRequest(rawPrompt));
    }

    [Fact]
    public void ExplicitFileMentionStillProvidesStructuredIdeContext()
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "Program.cs");
        File.WriteAllText(filePath, "class Program { }");
        using var service = new CodexProcessService();
        var prompt = "analise @Program.cs";

        var inputs = JArray.FromObject(service.BuildUserInput(
            prompt,
            new CodexExtensionSettings(),
            temp.Path,
            Array.Empty<string>(),
            "## Solution: C:/work/Archdraw"));

        Assert.Equal(prompt, inputs[0]?["text"]?.Value<string>());
        var mention = Assert.Single(
            inputs.OfType<JObject>(),
            item => item["type"]?.Value<string>() == "mention");
        Assert.Equal("Program.cs", mention["name"]?.Value<string>());
        Assert.Equal(filePath, mention["path"]?.Value<string>());
    }

    [Fact]
    public void ImageAttachmentIsSentInlineAsDataUrl()
    {
        using var temp = new TemporaryDirectory();
        var imagePath = Path.Combine(temp.Path, "clipboard.png");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        using var service = new CodexProcessService();

        var inputs = JArray.FromObject(service.BuildUserInput(
            "describe this image",
            new CodexExtensionSettings(),
            temp.Path,
            new[] { imagePath },
            string.Empty));

        var image = Assert.Single(
            inputs.OfType<JObject>(),
            item => item["type"]?.Value<string>() == "image");
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 }), image["url"]?.Value<string>());
    }

    [Fact]
    public void ClipboardImageAttachmentIsSentInlineAsDataUrl()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexVsixImages");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"clipboard_{Guid.NewGuid():N}.png");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(imagePath, bytes);

        try
        {
            using var service = new CodexProcessService();
            var inputs = JArray.FromObject(service.BuildUserInput(
                "describe this image",
                new CodexExtensionSettings(),
                directory,
                new[] { imagePath },
                string.Empty));

            var image = Assert.Single(
                inputs.OfType<JObject>(),
                item => item["type"]?.Value<string>() == "image");
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(bytes), image["url"]?.Value<string>());
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}

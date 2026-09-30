using System.Collections.Generic;
using System.IO;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexDiffArtifactControllerTests
{
    [Fact]
    public void AutomaticUpdatesDoNotCreateFilesOrOpenTabs()
    {
        using var temp = new TemporaryDirectory();
        var controller = new CodexDiffArtifactController(temp.Path,
            (_, _) => throw new System.Exception("No open artifact exists"),
            _ => throw new System.Exception("Automatic updates must not open a tab"));
        controller.UpdateIfOpen("thread", "diff");
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void UpdatesStayWithTheOpenDocumentAndConversationWithoutStealingFocus()
    {
        using var temp = new TemporaryDirectory();
        var open = new Dictionary<string, string>();
        var opens = 0;
        string? path = null;
        var controller = new CodexDiffArtifactController(temp.Path, (file, text) =>
        {
            if (!open.ContainsKey(file)) return false;
            open[file] = text;
            return true;
        }, file => { opens++; path = file; open[file] = File.ReadAllText(file); });

        controller.Show("a", "initial");
        controller.UpdateIfOpen("b", "wrong thread");
        Assert.Equal("initial", open[path!]);
        controller.UpdateIfOpen("a", "updated");
        Assert.Equal("updated", open[path!]);
        controller.UpdateIfOpen("a", "");
        Assert.Equal("", open[path!]);
        Assert.Equal(1, opens);
        Assert.Single(Directory.GetFiles(temp.Path));

        open.Clear();
        controller.UpdateIfOpen("a", "closed tab");
        Assert.Empty(open);
        Assert.Equal(1, opens);
        Assert.Equal("initial", File.ReadAllText(path!));
        controller.Show("a", "explicit reopen");
        Assert.Equal(2, opens);
        Assert.Equal("explicit reopen", open[path!]);
    }

    [Fact]
    public void RepeatedExplicitShowReusesTheOpenArtifact()
    {
        using var temp = new TemporaryDirectory();
        var current = "";
        var paths = new List<string>();
        var controller = new CodexDiffArtifactController(temp.Path,
            (_, content) => { current = content; return true; }, paths.Add);
        controller.Show("a", "first");
        controller.Show("a", "second");
        Assert.Equal(paths[0], paths[1]);
        Assert.Single(Directory.GetFiles(temp.Path));
        Assert.Equal("second", current);
    }
}

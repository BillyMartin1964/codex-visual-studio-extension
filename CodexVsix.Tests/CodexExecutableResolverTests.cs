using System.Collections.Generic;
using System.IO;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexExecutableResolverTests
{
    [Fact]
    public void LegacyInstallWinsEvenWhenANativeInstallExists()
    {
        var requests = new List<string>();
        var result = CodexExecutableResolver.ResolveExecutableLocationCore("codex.cmd", name =>
        {
            requests.Add(name);
            return name == "codex.cmd" ? "npm/codex.cmd" : "desktop/codex.exe";
        }, _ => throw new System.Exception("Already resolved"));
        Assert.Equal("npm/codex.cmd", result);
        Assert.Equal(new[] { "codex.cmd" }, requests);
    }

    [Fact]
    public void NativeInstallIsUsedOnlyAfterBothLegacyLookupsFail()
    {
        var requests = new List<string>();
        var result = CodexExecutableResolver.ResolveExecutableLocationCore("codex", name =>
        {
            requests.Add("path:" + name);
            return name == "codex.exe" ? "desktop/codex.exe" : "";
        }, name => { requests.Add("command:" + name); return ""; });
        Assert.Equal("desktop/codex.exe", result);
        Assert.Equal(new[] { "path:codex.cmd", "command:codex.cmd", "path:codex.exe" }, requests);
    }

    [Fact]
    public void ConfiguredPathsAreNeverReplacedByAnotherInstallation()
    {
        using var temp = new TemporaryDirectory();
        var explicitPath = Path.Combine(temp.Path, "custom.cmd");
        File.WriteAllText(explicitPath, "");
        Assert.Equal(explicitPath, CodexExecutableResolver.ResolveExecutableLocationCore(explicitPath,
            _ => throw new System.Exception("Explicit path"), _ => throw new System.Exception("Explicit path")));
        Assert.Equal("", CodexExecutableResolver.ResolveExecutableLocationCore(Path.Combine(temp.Path, "missing.cmd"),
            _ => throw new System.Exception("Explicit path"), _ => throw new System.Exception("Explicit path")));
    }

    [Theory]
    [InlineData("other.cmd")]
    [InlineData("codex.exe")]
    public void ExplicitCommandNamesAreNeverSubstituted(string command)
    {
        var requests = new List<string>();
        Assert.Equal("", CodexExecutableResolver.ResolveExecutableLocationCore(command,
            name => { requests.Add(name); return ""; }, name => { requests.Add(name); return ""; }));
        Assert.Equal(new[] { command, command }, requests);
    }
}

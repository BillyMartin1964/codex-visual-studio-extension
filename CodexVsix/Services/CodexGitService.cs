using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Implements the local Git portion of the Codex host protocol.  The response
/// shapes intentionally match the CyberVinci/Theia bridge consumed by the
/// official Codex webview.
/// </summary>
internal sealed class CodexGitService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<JObject> GetOriginsAsync(
        JObject values,
        string fallbackDirectory,
        CancellationToken cancellationToken)
    {
        var directories = (values["dirs"] as JArray)?
            .Values<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
        if (directories is null || directories.Length == 0)
        {
            directories = new[]
            {
                ExtractString(values, "cwd", "root", "gitRoot") ?? fallbackDirectory
            };
        }

        var origins = new JArray();
        foreach (var directory in directories)
        {
            var root = await ResolveGitRootAsync(directory, cancellationToken).ConfigureAwait(false);
            string? originUrl = null;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var originResult = await RunGitAsync(
                    root!,
                    new[] { "config", "--get", "remote.origin.url" },
                    cancellationToken).ConfigureAwait(false);
                originUrl = originResult.Success ? NullIfEmpty(originResult.Stdout) : null;
            }

            origins.Add(new JObject
            {
                ["dir"] = directory,
                ["root"] = root is null ? JValue.CreateNull() : root,
                ["originUrl"] = originUrl is null ? JValue.CreateNull() : originUrl
            });
        }

        return new JObject
        {
            ["origins"] = origins,
            ["homeDir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
    }

    public async Task<JToken?> HandleHostRequestAsync(
        string method,
        JObject values,
        string fallbackDirectory,
        CancellationToken cancellationToken)
    {
        var cwd = ExtractString(values, "cwd", "root", "gitRoot") ?? fallbackDirectory;
        switch (method)
        {
            case "git-origins":
                return await GetOriginsAsync(values, fallbackDirectory, cancellationToken).ConfigureAwait(false);

            case "git-merge-base": {
                var baseBranch = ExtractString(values, "baseBranch", "base", "upstream");
                if (string.IsNullOrWhiteSpace(baseBranch))
                {
                    return new JObject { ["mergeBaseSha"] = JValue.CreateNull() };
                }

                var result = await RunGitAsync(
                    cwd,
                    new[] { "merge-base", "HEAD", baseBranch! },
                    cancellationToken).ConfigureAwait(false);
                return new JObject
                {
                    ["mergeBaseSha"] = ToNullableValue(result.Success ? NullIfEmpty(result.Stdout) : null)
                };
            }

            case "git-create-branch": {
                var branch = ExtractString(values, "branchName", "branch");
                if (string.IsNullOrWhiteSpace(branch))
                {
                    return new JObject { ["success"] = false, ["error"] = "Missing branch name" };
                }

                var result = await RunGitAsync(cwd, new[] { "branch", branch! }, cancellationToken).ConfigureAwait(false);
                return BuildCommandResult(result, branch);
            }

            case "git-checkout-branch": {
                var branch = ExtractString(values, "branchName", "branch", "ref");
                if (string.IsNullOrWhiteSpace(branch))
                {
                    return new JObject { ["success"] = false, ["error"] = "Missing branch name" };
                }

                var result = await RunGitAsync(cwd, new[] { "checkout", branch! }, cancellationToken).ConfigureAwait(false);
                return BuildCommandResult(result, branch);
            }

            case "git-push": {
                var arguments = new List<string> { "push", ExtractString(values, "remote") ?? "origin" };
                var branch = ExtractString(values, "branch", "branchName");
                if (!string.IsNullOrWhiteSpace(branch))
                {
                    arguments.Add(branch!);
                }

                var result = await RunGitAsync(cwd, arguments, cancellationToken).ConfigureAwait(false);
                return BuildCommandResult(result, branch);
            }

            case "apply-patch":
                return await ApplyPatchAsync(values, cwd, cancellationToken).ConfigureAwait(false);

            case "codex-worktrees":
            case "list-worktrees": {
                var worktrees = await ListWorktreesAsync(cwd, cancellationToken).ConfigureAwait(false);
                return new JObject { ["worktrees"] = worktrees };
            }

            case "prepare-worktree-snapshot":
                return await PrepareWorktreeSnapshotAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "upload-worktree-snapshot":
                return await UploadWorktreeSnapshotAsync(values, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException("Unknown Git host method: " + method);
        }
    }

    public async Task<JToken?> HandleWorkerRequestAsync(
        string method,
        JObject values,
        string fallbackDirectory,
        CancellationToken cancellationToken)
    {
        var cwd = ExtractString(values, "cwd", "root", "gitRoot") ?? fallbackDirectory;
        switch (method)
        {
            case "stable-metadata":
                return await GetStableMetadataAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "list-worktrees":
            case "codex-worktrees":
                return new JObject
                {
                    ["worktrees"] = await ListWorktreesAsync(cwd, cancellationToken).ConfigureAwait(false)
                };

            case "current-branch":
                return new JObject
                {
                    ["branch"] = ToNullableValue(await CurrentBranchAsync(cwd, cancellationToken).ConfigureAwait(false))
                };

            case "default-branch":
            case "base-branch":
                return new JObject
                {
                    ["branch"] = ToNullableValue(await DefaultBranchAsync(cwd, cancellationToken).ConfigureAwait(false))
                };

            case "upstream-branch":
                return new JObject
                {
                    ["branch"] = ToNullableValue(await UpstreamBranchAsync(cwd, cancellationToken).ConfigureAwait(false))
                };

            case "branch-ahead-count":
                return await GetAheadBehindAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "recent-branches":
                return await GetRecentBranchesAsync(cwd, ReadLimit(values, 10, 100), cancellationToken).ConfigureAwait(false);

            case "branch-exists":
                return new JObject
                {
                    ["exists"] = await BranchExistsAsync(
                        cwd,
                        ExtractString(values, "branch", "branchName", "ref") ?? string.Empty,
                        cancellationToken).ConfigureAwait(false)
                };

            case "branch-commits":
                return new JObject
                {
                    ["commits"] = await GetBranchCommitsAsync(cwd, values, cancellationToken).ConfigureAwait(false)
                };

            case "search-branches":
                return new JObject
                {
                    ["branches"] = await SearchBranchesAsync(
                        cwd,
                        ExtractString(values, "query", "search", "prefix") ?? string.Empty,
                        ReadLimit(values, 20, 100),
                        cancellationToken).ConfigureAwait(false)
                };

            case "nearest-ancestor-branch":
                return new JObject
                {
                    ["branch"] = ToNullableValue(await FindNearestAncestorBranchAsync(
                        cwd,
                        ExtractStringArray(values["candidates"]),
                        ExtractString(values, "currentBranch", "branch")
                            ?? await CurrentBranchAsync(cwd, cancellationToken).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false))
                };

            case "branch-metadata": {
                var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false);
                var upstream = await UpstreamBranchAsync(cwd, cancellationToken).ConfigureAwait(false);
                var baseBranch = await DefaultBranchAsync(cwd, cancellationToken).ConfigureAwait(false);
                var separator = upstream?.IndexOf('/') ?? -1;
                return new JObject
                {
                    ["gitRoot"] = ToNullableValue(root),
                    ["branch"] = ToNullableValue(await CurrentBranchAsync(cwd, cancellationToken).ConfigureAwait(false)),
                    ["baseBranch"] = ToNullableValue(baseBranch),
                    ["baseBranchRemote"] = separator > 0 ? upstream!.Substring(0, separator) : "origin"
                };
            }

            case "status-summary":
                return await GetStatusSummaryAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "branch-diff-stats":
                return await GetBranchDiffStatsAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "review-summary":
                return await GetReviewSummaryAsync(cwd, values, includeStageCounts: true, cancellationToken).ConfigureAwait(false);

            case "review-path-summary":
                return await GetReviewSummaryAsync(cwd, values, includeStageCounts: false, cancellationToken).ConfigureAwait(false);

            case "review-diff":
                return await GetReviewDiffAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "review-search":
                return await GetReviewSearchAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "review-patch":
                return await GetReviewPatchAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "commit-message-diff":
                return await GetCommitMessageDiffAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "submodule-paths": {
                var result = await RunGitAsync(
                    cwd,
                    new[] { "config", "--file", ".gitmodules", "--get-regexp", "path" },
                    cancellationToken).ConfigureAwait(false);
                var paths = result.Success
                    ? result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => path!)
                        .Distinct(StringComparer.Ordinal)
                    : Enumerable.Empty<string>();
                return new JObject { ["paths"] = new JArray(paths) };
            }

            case "cat-file":
                return await CatFileAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "blame-file":
                return await BlameFileAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "synced-branch":
                return new JObject
                {
                    ["branch"] = JValue.CreateNull(),
                    ["base"] = JValue.CreateNull(),
                    ["hasConflicts"] = await HasMergeConflictsAsync(cwd, cancellationToken).ConfigureAwait(false)
                };

            case "synced-branch-state":
                return await GetSyncedBranchStateAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "git-origins":
                return await GetOriginsAsync(values, fallbackDirectory, cancellationToken).ConfigureAwait(false);

            case "config-value": {
                var key = ExtractString(values, "key");
                if (string.IsNullOrWhiteSpace(key))
                {
                    return new JObject { ["value"] = JValue.CreateNull() };
                }

                var arguments = new List<string> { "config" };
                if (string.Equals(values["scope"]?.Value<string>(), "global", StringComparison.Ordinal))
                {
                    arguments.Add("--global");
                }
                arguments.Add("--get");
                arguments.Add(key!);
                var result = await RunGitAsync(cwd, arguments, cancellationToken).ConfigureAwait(false);
                return new JObject { ["value"] = ToNullableValue(result.Success ? NullIfEmpty(result.Stdout) : null) };
            }

            case "set-config-value":
                return new JObject
                {
                    ["success"] = await SetConfigValueAsync(cwd, values, cancellationToken).ConfigureAwait(false)
                };

            case "index-info":
                return await GetIndexInfoAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "worktree-snapshot-ref": {
                var worktreePath = ExtractString(values, "worktreePath", "cwd", "root", "gitRoot") ?? cwd;
                var result = await RunGitAsync(worktreePath, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
                return new JObject { ["ref"] = ToNullableValue(result.Success ? NullIfEmpty(result.Stdout) : null) };
            }

            case "set-worktree-owner-thread":
                return new JObject { ["success"] = true };

            case "git-init-repo":
                return await InitializeRepositoryAsync(cwd, cancellationToken).ConfigureAwait(false);

            case "commit":
                return await CommitAsync(cwd, values, cancellationToken).ConfigureAwait(false);

            case "watch-repo":
            case "unwatch-repo":
            case "invalidate-untracked-paths-cache":
            case "invalidate-stable-metadata":
            case "dispose-git-init-watch":
                return new JObject { ["ok"] = true };

            default:
                throw new NotSupportedException("Unknown Git worker method: " + method);
        }
    }

    private async Task<JObject> GetStableMetadataAsync(string cwd, CancellationToken cancellationToken)
    {
        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return new JObject
            {
                ["isRepo"] = false,
                ["root"] = JValue.CreateNull(),
                ["commonDir"] = JValue.CreateNull(),
                ["gitRoot"] = JValue.CreateNull(),
                ["branch"] = JValue.CreateNull(),
                ["defaultBranch"] = JValue.CreateNull(),
                ["upstreamBranch"] = JValue.CreateNull(),
                ["aheadCount"] = 0,
                ["behindCount"] = 0
            };
        }

        var branch = await CurrentBranchAsync(root, cancellationToken).ConfigureAwait(false);
        var defaultBranch = await DefaultBranchAsync(root, cancellationToken).ConfigureAwait(false);
        var upstream = await UpstreamBranchAsync(root, cancellationToken).ConfigureAwait(false);
        var counts = await GetAheadBehindAsync(root, cancellationToken).ConfigureAwait(false);
        var commonDirResult = await RunGitAsync(
            root,
            new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" },
            cancellationToken).ConfigureAwait(false);
        var commonDir = commonDirResult.Success ? NullIfEmpty(commonDirResult.Stdout) : null;
        return new JObject
        {
            ["isRepo"] = true,
            ["root"] = root,
            ["commonDir"] = commonDir ?? Path.Combine(root, ".git"),
            ["gitRoot"] = root,
            ["branch"] = ToNullableValue(branch),
            ["defaultBranch"] = ToNullableValue(defaultBranch),
            ["upstreamBranch"] = ToNullableValue(upstream),
            ["aheadCount"] = counts["aheadCount"]?.Value<int>() ?? 0,
            ["behindCount"] = counts["behindCount"]?.Value<int>() ?? 0
        };
    }

    private async Task<JArray> GetBranchCommitsAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var limit = ReadLimit(values, 50, 200);
        var baseBranch = ExtractString(values, "baseBranch", "base", "upstream");
        var branch = ExtractString(values, "branch", "currentBranch", "head") ?? "HEAD";
        var range = string.IsNullOrWhiteSpace(baseBranch) ? branch : baseBranch + ".." + branch;
        var result = await RunGitAsync(
            cwd,
            new[]
            {
                "log",
                "--max-count=" + limit.ToString(CultureInfo.InvariantCulture),
                "--format=%H%x00%h%x00%an%x00%ae%x00%aI%x00%s%x1e",
                range
            },
            cancellationToken).ConfigureAwait(false);
        var commits = new JArray();
        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return commits;
        }

        foreach (var entry in result.Stdout.Split(new[] { '\x1e' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = entry.Trim().Split(new[] { '\0' }, StringSplitOptions.None);
            if (pieces.Length < 5)
            {
                continue;
            }
            commits.Add(new JObject
            {
                ["sha"] = pieces[0],
                ["shortSha"] = pieces[1],
                ["authorName"] = pieces[2],
                ["authorEmail"] = pieces[3],
                ["committedAt"] = pieces[4],
                ["subject"] = pieces.Length > 5 ? pieces[5] : string.Empty
            });
        }
        return commits;
    }

    private async Task<JArray> SearchBranchesAsync(
        string cwd,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            cwd,
            new[]
            {
                "for-each-ref", "--sort=-committerdate", "refs/heads", "refs/remotes", "--format=%(refname:short)"
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new JArray();
        }

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var branches = result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Select(value => value.StartsWith("origin/", StringComparison.Ordinal) ? value.Substring("origin/".Length) : value)
            .Where(value => value != "HEAD" && !value.EndsWith("/HEAD", StringComparison.Ordinal))
            .Where(value => normalizedQuery.Length == 0 || value.ToLowerInvariant().Contains(normalizedQuery))
            .Distinct(StringComparer.Ordinal)
            .Take(limit);
        return new JArray(branches);
    }

    private async Task<string?> FindNearestAncestorBranchAsync(
        string cwd,
        IEnumerable<string> candidates,
        string? currentBranch,
        CancellationToken cancellationToken)
    {
        string? bestBranch = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, currentBranch, StringComparison.Ordinal)
                || !await BranchExistsAsync(cwd, candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var mergeBase = await RunGitAsync(
                cwd,
                new[] { "merge-base", "HEAD", candidate },
                cancellationToken).ConfigureAwait(false);
            var sha = mergeBase.Success ? NullIfEmpty(mergeBase.Stdout) : null;
            if (sha is null)
            {
                continue;
            }
            var distanceResult = await RunGitAsync(
                cwd,
                new[] { "rev-list", "--count", sha + "..HEAD" },
                cancellationToken).ConfigureAwait(false);
            if (distanceResult.Success
                && int.TryParse(distanceResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance)
                && distance < bestDistance)
            {
                bestBranch = candidate;
                bestDistance = distance;
            }
        }
        return bestBranch;
    }

    private async Task<JToken> GetBranchDiffStatsAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var arguments = await BuildDiffArgumentsAsync(cwd, values, "branch", cancellationToken).ConfigureAwait(false);
        if (arguments is null)
        {
            return JValue.CreateNull();
        }
        var result = await RunGitAsync(
            cwd,
            new[] { "diff", "--numstat" }.Concat(HideWhitespaceArguments(values)).Concat(arguments),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return JValue.CreateNull();
        }
        var stats = ParseNumstat(result.Stdout);
        return new JObject
        {
            ["additions"] = stats.LinesAdded,
            ["deletions"] = stats.LinesRemoved,
            ["fileCount"] = stats.FilesChanged
        };
    }

    private async Task<JObject> GetReviewSummaryAsync(
        string cwd,
        JObject values,
        bool includeStageCounts,
        CancellationToken cancellationToken)
    {
        var source = ExtractString(values, "source") ?? "branch";
        var files = await GetReviewFilesAsync(cwd, values, cancellationToken).ConfigureAwait(false);
        if (files is null)
        {
            return new JObject { ["type"] = "error", ["source"] = source };
        }
        var response = new JObject
        {
            ["type"] = "success",
            ["source"] = source,
            ["files"] = files
        };
        if (includeStageCounts)
        {
            var status = await GetStatusSummaryAsync(cwd, cancellationToken).ConfigureAwait(false);
            response["stageCounts"] = new JObject
            {
                ["stagedFileCount"] = status["type"]?.Value<string>() == "success" ? status["stagedCount"]?.Value<int>() ?? 0 : 0,
                ["unstagedFileCount"] = status["type"]?.Value<string>() == "success" ? status["unstagedCount"]?.Value<int>() ?? 0 : 0,
                ["untrackedFileCount"] = status["type"]?.Value<string>() == "success" ? status["untrackedCount"]?.Value<int>() ?? 0 : 0
            };
        }
        return response;
    }

    private async Task<JObject> GetReviewDiffAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var source = ExtractString(values, "source") ?? "branch";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (values["files"] is JArray files)
        {
            foreach (var file in files.OfType<JObject>())
            {
                var path = ExtractString(file, "path");
                if (path is not null)
                {
                    paths.Add(path);
                }
            }
        }
        foreach (var path in ExtractStringArray(values["paths"]))
        {
            paths.Add(path);
        }

        var diffs = new JObject();
        foreach (var path in paths)
        {
            diffs[path] = await GetSingleFileDiffAsync(cwd, values, path, cancellationToken).ConfigureAwait(false);
        }
        return new JObject { ["source"] = source, ["diffs"] = diffs };
    }

    private async Task<JObject> GetReviewSearchAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var source = ExtractString(values, "source") ?? "branch";
        var query = ExtractString(values, "query") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new JObject
            {
                ["type"] = "success",
                ["source"] = source,
                ["query"] = query,
                ["matches"] = new JArray(),
                ["totalMatches"] = 0,
                ["isCapped"] = false
            };
        }

        var files = await GetReviewFilesAsync(cwd, values, cancellationToken).ConfigureAwait(false) ?? new JArray();
        var matches = new JArray();
        var total = 0;
        foreach (var file in files.OfType<JObject>())
        {
            var path = file["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var diff = await GetSingleFileDiffAsync(cwd, values, path!, cancellationToken).ConfigureAwait(false);
            if (diff["type"]?.Value<string>() != "success")
            {
                continue;
            }
            var lines = (diff["diff"]?.Value<string>() ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                total++;
                if (matches.Count < 200)
                {
                    matches.Add(new JObject { ["path"] = path, ["lineNumber"] = index + 1, ["line"] = lines[index] });
                }
            }
        }
        return new JObject
        {
            ["type"] = "success",
            ["source"] = source,
            ["query"] = query,
            ["matches"] = matches,
            ["totalMatches"] = total,
            ["isCapped"] = total > 200
        };
    }

    private async Task<JObject> GetReviewPatchAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var source = ExtractString(values, "source") ?? "branch";
        var arguments = await BuildDiffArgumentsAsync(cwd, values, source, cancellationToken).ConfigureAwait(false);
        if (arguments is null)
        {
            return BuildReviewPatchError(source);
        }
        var result = await RunGitAsync(
            cwd,
            new[] { "diff" }.Concat(HideWhitespaceArguments(values)).Concat(arguments),
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new JObject
            {
                ["source"] = source,
                ["diff"] = new JObject
                {
                    ["type"] = "success",
                    ["unifiedDiff"] = result.Stdout,
                    ["unifiedDiffBytes"] = Encoding.UTF8.GetByteCount(result.Stdout)
                }
            }
            : BuildReviewPatchError(source);
    }

    private static JObject BuildReviewPatchError(string source)
    {
        return new JObject
        {
            ["source"] = source,
            ["diff"] = new JObject
            {
                ["type"] = "error",
                ["error"] = new JObject { ["type"] = "unknown" }
            }
        };
    }

    private async Task<JObject> GetCommitMessageDiffAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var staged = await RunGitAsync(cwd, new[] { "diff", "--cached" }, cancellationToken).ConfigureAwait(false);
        if (staged.Success && !string.IsNullOrWhiteSpace(staged.Stdout))
        {
            parts.Add(staged.Stdout);
        }
        if (values["includeUnstaged"]?.Value<bool>() == true)
        {
            var unstaged = await RunGitAsync(cwd, new[] { "diff" }, cancellationToken).ConfigureAwait(false);
            if (unstaged.Success && !string.IsNullOrWhiteSpace(unstaged.Stdout))
            {
                parts.Add(unstaged.Stdout);
            }
        }
        var unifiedDiff = string.Join(Environment.NewLine, parts);
        return new JObject
        {
            ["type"] = "success",
            ["unifiedDiff"] = unifiedDiff,
            ["unifiedDiffBytes"] = Encoding.UTF8.GetByteCount(unifiedDiff)
        };
    }

    private async Task<JArray?> GetReviewFilesAsync(
        string cwd,
        JObject values,
        CancellationToken cancellationToken)
    {
        var source = ExtractString(values, "source") ?? "branch";
        var arguments = await BuildDiffArgumentsAsync(cwd, values, source, cancellationToken).ConfigureAwait(false);
        if (arguments is null)
        {
            return null;
        }
        var common = HideWhitespaceArguments(values).Concat(new[] { "--find-renames" });
        var nameStatus = await RunGitAsync(
            cwd,
            new[] { "diff" }.Concat(common).Concat(new[] { "--name-status", "-z" }).Concat(arguments),
            cancellationToken).ConfigureAwait(false);
        var numstat = await RunGitAsync(
            cwd,
            new[] { "diff" }.Concat(HideWhitespaceArguments(values)).Concat(new[] { "--find-renames", "--numstat", "-z" }).Concat(arguments),
            cancellationToken).ConfigureAwait(false);
        if (!nameStatus.Success || !numstat.Success)
        {
            return null;
        }

        var files = ParseNameStatus(nameStatus.Stdout);
        var stats = ParseNumstatEntries(numstat.Stdout).ToDictionary(
            item => DiffFileKey(item.Path, item.PreviousPath),
            item => item,
            StringComparer.Ordinal);
        foreach (var file in files.OfType<JObject>())
        {
            var path = file["path"]?.Value<string>() ?? string.Empty;
            var previous = file["previousPath"]?.Type == JTokenType.Null ? null : file["previousPath"]?.Value<string>();
            stats.TryGetValue(DiffFileKey(path, previous), out var stat);
            file["additions"] = stat?.Additions is int additions ? additions : JValue.CreateNull();
            file["deletions"] = stat?.Deletions is int deletions ? deletions : JValue.CreateNull();
            file["revision"] = source + ":" + (file["changeKind"]?.Value<string>() ?? "modified")
                + ":" + (previous ?? string.Empty) + ":" + path;
        }
        return files;
    }

    private async Task<JObject> GetSingleFileDiffAsync(
        string cwd,
        JObject values,
        string path,
        CancellationToken cancellationToken)
    {
        var scopedValues = (JObject)values.DeepClone();
        scopedValues["paths"] = new JArray(path);
        var source = ExtractString(scopedValues, "source") ?? "branch";
        var arguments = await BuildDiffArgumentsAsync(cwd, scopedValues, source, cancellationToken).ConfigureAwait(false);
        if (arguments is null)
        {
            return new JObject { ["type"] = "error", ["error"] = new JObject { ["type"] = "unknown" } };
        }
        var result = await RunGitAsync(
            cwd,
            new[] { "diff" }.Concat(HideWhitespaceArguments(scopedValues)).Concat(new[] { "--find-renames" }).Concat(arguments),
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new JObject
            {
                ["type"] = "success",
                ["diff"] = result.Stdout,
                ["diffBytes"] = Encoding.UTF8.GetByteCount(result.Stdout)
            }
            : new JObject { ["type"] = "error", ["error"] = new JObject { ["type"] = "unknown" } };
    }

    private async Task<List<string>?> BuildDiffArgumentsAsync(
        string cwd,
        JObject values,
        string source,
        CancellationToken cancellationToken)
    {
        var paths = ExtractStringArray(values["paths"]);
        var pathArguments = paths.Count > 0
            ? new[] { "--" }.Concat(paths).ToList()
            : new List<string>();
        if (source == "staged")
        {
            return new[] { "--cached" }.Concat(pathArguments).ToList();
        }
        if (source == "unstaged")
        {
            return pathArguments;
        }
        if (source == "commit")
        {
            var commit = ExtractString(values, "commitSha", "commit", "sha");
            return commit is null ? null : new[] { commit + "^!" }.Concat(pathArguments).ToList();
        }
        var commitSha = ExtractString(values, "commitSha", "commit", "sha");
        if (commitSha is not null)
        {
            return new[] { commitSha + "..HEAD" }.Concat(pathArguments).ToList();
        }
        var baseBranch = ExtractString(values, "baseBranch", "base", "upstream")
            ?? await DefaultBranchAsync(cwd, cancellationToken).ConfigureAwait(false);
        return baseBranch is null
            ? new[] { "HEAD" }.Concat(pathArguments).ToList()
            : new[] { baseBranch + "...HEAD" }.Concat(pathArguments).ToList();
    }

    private static IEnumerable<string> HideWhitespaceArguments(JObject values)
    {
        return values["hideWhitespace"]?.Value<bool>() == true
            ? new[] { "--ignore-all-space" }
            : Array.Empty<string>();
    }

    private static JArray ParseNameStatus(string output)
    {
        var entries = output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        var files = new JArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var status = entries[index];
            var tab = status.IndexOf('\t');
            if (tab >= 0)
            {
                var codeValue = status.Substring(0, tab);
                var embeddedPath = status.Substring(tab + 1);
                files.Add(new JObject
                {
                    ["path"] = embeddedPath,
                    ["previousPath"] = JValue.CreateNull(),
                    ["changeKind"] = NormalizeChangeKind(codeValue.Length > 0 ? codeValue[0] : 'M')
                });
                continue;
            }
            var code = status.Length > 0 ? status[0] : 'M';
            if ((code == 'R' || code == 'C') && index + 2 < entries.Length)
            {
                files.Add(new JObject
                {
                    ["path"] = entries[index + 2],
                    ["previousPath"] = entries[index + 1],
                    ["changeKind"] = NormalizeChangeKind(code)
                });
                index += 2;
                continue;
            }
            if (index + 1 < entries.Length)
            {
                files.Add(new JObject
                {
                    ["path"] = entries[++index],
                    ["previousPath"] = JValue.CreateNull(),
                    ["changeKind"] = NormalizeChangeKind(code)
                });
            }
        }
        return files;
    }

    private static List<NumstatEntry> ParseNumstatEntries(string output)
    {
        var entries = output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        var stats = new List<NumstatEntry>();
        for (var index = 0; index < entries.Length; index++)
        {
            var pieces = entries[index].Split('\t');
            if (pieces.Length < 3)
            {
                continue;
            }
            var additions = ParseDiffCount(pieces[0]);
            var deletions = ParseDiffCount(pieces[1]);
            if (pieces[2].Length > 0)
            {
                stats.Add(new NumstatEntry(pieces[2], null, additions, deletions));
            }
            else if (index + 2 < entries.Length)
            {
                stats.Add(new NumstatEntry(entries[index + 2], entries[index + 1], additions, deletions));
                index += 2;
            }
        }
        return stats;
    }

    private static DiffStats ParseNumstat(string output)
    {
        var entries = output.IndexOf('\0') >= 0
            ? ParseNumstatEntries(output)
            : output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(pieces => pieces.Length >= 3)
                .Select(pieces => new NumstatEntry(pieces[2], null, ParseDiffCount(pieces[0]), ParseDiffCount(pieces[1])))
                .ToList();
        return new DiffStats(
            entries.Count,
            entries.Sum(entry => entry.Additions ?? 0),
            entries.Sum(entry => entry.Deletions ?? 0));
    }

    private static int? ParseDiffCount(string value)
    {
        return value == "-" || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? null
            : parsed;
    }

    private static string DiffFileKey(string path, string? previousPath)
    {
        return (previousPath ?? string.Empty) + "\0" + path;
    }

    private static string NormalizeChangeKind(char status)
    {
        switch (status)
        {
            case 'A': return "added";
            case 'D': return "deleted";
            case 'R': return "renamed";
            case 'C': return "copied";
            case 'T': return "type-changed";
            case 'U': return "unmerged";
            default: return "modified";
        }
    }

    private async Task<JObject> CatFileAsync(string cwd, JObject values, CancellationToken cancellationToken)
    {
        var oid = ExtractString(values, "oid", "sha", "ref") ?? "HEAD";
        var path = ExtractString(values, "path", "filePath");
        if (path is null)
        {
            return BuildNotFoundGitResult();
        }
        var result = await RunGitAsync(cwd, new[] { "show", oid + ":" + path }, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return new JObject { ["type"] = "success", ["contents"] = result.Stdout, ["content"] = result.Stdout };
        }
        if (values["fallbackToDisk"]?.Value<bool>() == true)
        {
            var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false) ?? cwd;
            var candidate = Path.GetFullPath(Path.Combine(root, path));
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                var contents = File.ReadAllText(candidate);
                return new JObject { ["type"] = "success", ["contents"] = contents, ["content"] = contents };
            }
        }
        return BuildNotFoundGitResult();
    }

    private async Task<JObject> BlameFileAsync(string cwd, JObject values, CancellationToken cancellationToken)
    {
        var path = ExtractString(values, "path", "filePath");
        if (path is null)
        {
            return BuildNotFoundGitResult();
        }
        var result = await RunGitAsync(
            cwd,
            new[] { "blame", "--line-porcelain", "--", path },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return BuildNotFoundGitResult();
        }
        return new JObject
        {
            ["type"] = "success",
            ["lines"] = ParseBlamePorcelain(result.Stdout),
            ["repositoryWebUrl"] = ToNullableValue(await GetRepositoryWebUrlAsync(cwd, cancellationToken).ConfigureAwait(false))
        };
    }

    private static JObject BuildNotFoundGitResult()
    {
        return new JObject { ["type"] = "error", ["error"] = new JObject { ["type"] = "not-found" } };
    }

    private static JArray ParseBlamePorcelain(string output)
    {
        var lines = new JArray();
        JObject? current = null;
        foreach (var rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var pieces = rawLine.Split(' ');
            if (pieces.Length >= 3 && pieces[0].Length >= 7 && pieces[0].All(Uri.IsHexDigit)
                && int.TryParse(pieces[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                current = new JObject { ["sha"] = pieces[0], ["lineNumber"] = lineNumber };
                continue;
            }
            if (current is null)
            {
                continue;
            }
            if (rawLine.StartsWith("author ", StringComparison.Ordinal))
            {
                current["author"] = rawLine.Substring("author ".Length);
            }
            else if (rawLine.StartsWith("author-time ", StringComparison.Ordinal)
                && long.TryParse(rawLine.Substring("author-time ".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                current["committedAt"] = DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);
            }
            else if (rawLine.StartsWith("summary ", StringComparison.Ordinal))
            {
                current["subject"] = rawLine.Substring("summary ".Length);
            }
            else if (rawLine.StartsWith("\t", StringComparison.Ordinal))
            {
                current["text"] = rawLine.Substring(1);
                lines.Add(current);
                current = null;
            }
        }
        return lines;
    }

    private async Task<string?> GetRepositoryWebUrlAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            cwd,
            new[] { "config", "--get", "remote.origin.url" },
            cancellationToken).ConfigureAwait(false);
        var origin = result.Success ? NullIfEmpty(result.Stdout) : null;
        if (origin is null)
        {
            return null;
        }
        if (origin.StartsWith("git@", StringComparison.Ordinal))
        {
            var separator = origin.IndexOf(':');
            if (separator > 4)
            {
                return "https://" + origin.Substring(4, separator - 4) + "/"
                    + TrimGitSuffix(origin.Substring(separator + 1));
            }
        }
        return TrimGitSuffix(origin);
    }

    private async Task<bool> HasMergeConflictsAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(cwd, new[] { "ls-files", "-u" }, cancellationToken).ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout);
    }

    private async Task<JObject> GetSyncedBranchStateAsync(string cwd, CancellationToken cancellationToken)
    {
        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false);
        var headResult = await RunGitAsync(cwd, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
        var emptyStats = new JObject { ["filesChanged"] = 0, ["linesAdded"] = 0, ["linesRemoved"] = 0 };
        return new JObject
        {
            ["branch"] = JValue.CreateNull(),
            ["worktreeSnapshot"] = new JObject
            {
                ["root"] = ToNullableValue(root),
                ["headCommitSha"] = ToNullableValue(headResult.Success ? NullIfEmpty(headResult.Stdout) : null),
                ["workingTreeRef"] = JValue.CreateNull()
            },
            ["branchSnapshot"] = new JObject { ["checkedOut"] = false, ["headCommitSha"] = JValue.CreateNull() },
            ["localCommitsAhead"] = 0,
            ["worktreeCommitsAhead"] = 0,
            ["localUncommittedDiffStats"] = emptyStats.DeepClone(),
            ["worktreeUncommittedDiffStats"] = emptyStats
        };
    }

    private async Task<bool> SetConfigValueAsync(string cwd, JObject values, CancellationToken cancellationToken)
    {
        var key = ExtractString(values, "key");
        if (key is null)
        {
            return false;
        }
        var arguments = new List<string> { "config" };
        if (values["scope"]?.Value<string>() == "global")
        {
            arguments.Add("--global");
        }
        arguments.Add(key);
        var valueToken = values["value"];
        arguments.Add(valueToken is null
            || valueToken.Type == JTokenType.Null
            || valueToken.Type == JTokenType.Undefined
                ? string.Empty
                : valueToken.ToString());
        return (await RunGitAsync(cwd, arguments, cancellationToken).ConfigureAwait(false)).Success;
    }

    private async Task<JObject> InitializeRepositoryAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(cwd, new[] { "init" }, cancellationToken).ConfigureAwait(false);
        return BuildCommandResult(result, branch: null);
    }

    private async Task<JObject> CommitAsync(string cwd, JObject values, CancellationToken cancellationToken)
    {
        if (values["includeUnstaged"]?.Value<bool>() == true)
        {
            var add = await RunGitAsync(cwd, new[] { "add", "-A" }, cancellationToken).ConfigureAwait(false);
            if (!add.Success)
            {
                return BuildCommitError(add);
            }
        }
        var commit = await RunGitAsync(
            cwd,
            new[] { "commit", "-m", ExtractString(values, "message") ?? "Update from Codex" },
            cancellationToken).ConfigureAwait(false);
        if (!commit.Success)
        {
            return BuildCommitError(commit);
        }
        var sha = await RunGitAsync(cwd, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
        return new JObject
        {
            ["status"] = "success",
            ["commitSha"] = ToNullableValue(sha.Success ? NullIfEmpty(sha.Stdout) : null)
        };
    }

    private static JObject BuildCommitError(GitResult result)
    {
        var output = NullIfEmpty(result.Stderr) ?? result.Stdout;
        return new JObject
        {
            ["status"] = "error",
            ["error"] = result.Error ?? output,
            ["execOutput"] = new JObject { ["output"] = output }
        };
    }

    private async Task<JObject> ApplyPatchAsync(
        JObject values,
        string cwd,
        CancellationToken cancellationToken)
    {
        var patch = ExtractString(values, "patch", "unifiedDiff", "diff", "content");
        if (string.IsNullOrWhiteSpace(patch) && values["patches"] is JArray patches)
        {
            patch = string.Join(
                Environment.NewLine,
                patches.Select(value => value.Type == JTokenType.String
                    ? value.Value<string>()
                    : ExtractString(value as JObject ?? new JObject(), "patch", "unifiedDiff", "diff", "content"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (string.IsNullOrWhiteSpace(patch))
        {
            return new JObject
            {
                ["success"] = false,
                ["appliedPaths"] = new JArray(),
                ["skippedPaths"] = new JArray(),
                ["conflictedPaths"] = new JArray(),
                ["error"] = "Missing patch payload"
            };
        }

        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false) ?? cwd;
        var patchPath = Path.Combine(
            Path.GetTempPath(),
            "codex-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".patch");
        var patchPaths = ExtractPatchPaths(patch!);
        File.WriteAllText(patchPath, patch!, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            var check = await RunGitAsync(
                root,
                new[] { "apply", "--check", patchPath },
                cancellationToken).ConfigureAwait(false);
            if (!check.Success)
            {
                return BuildPatchResult(false, Array.Empty<string>(), patchPaths, check);
            }

            var apply = await RunGitAsync(
                root,
                new[] { "apply", "--whitespace=nowarn", patchPath },
                cancellationToken).ConfigureAwait(false);
            return apply.Success
                ? BuildPatchResult(true, patchPaths, Array.Empty<string>(), apply)
                : BuildPatchResult(false, Array.Empty<string>(), patchPaths, apply);
        }
        finally
        {
            try
            {
                File.Delete(patchPath);
            }
            catch
            {
            }
        }
    }

    private async Task<JArray> ListWorktreesAsync(string cwd, CancellationToken cancellationToken)
    {
        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return new JArray();
        }

        var result = await RunGitAsync(
            root,
            new[] { "worktree", "list", "--porcelain" },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new JArray();
        }

        var output = new JArray();
        JObject? current = null;
        foreach (var rawLine in result.Stdout.Split(new[] { '\n' }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    output.Add(current);
                }

                current = new JObject
                {
                    ["path"] = Path.GetFullPath(line.Substring("worktree ".Length)),
                    ["head"] = string.Empty,
                    ["branch"] = JValue.CreateNull(),
                    ["bare"] = false,
                    ["locked"] = false,
                    ["prunable"] = false
                };
            }
            else if (current is not null && line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                current["head"] = line.Substring("HEAD ".Length);
            }
            else if (current is not null && line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branch = line.Substring("branch ".Length);
                const string prefix = "refs/heads/";
                current["branch"] = branch.StartsWith(prefix, StringComparison.Ordinal)
                    ? branch.Substring(prefix.Length)
                    : branch;
            }
            else if (current is not null && (line == "bare" || line == "locked" || line == "prunable"))
            {
                current[line] = true;
            }
        }

        if (current is not null)
        {
            output.Add(current);
        }

        return output;
    }

    private async Task<JObject> PrepareWorktreeSnapshotAsync(string cwd, CancellationToken cancellationToken)
    {
        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false) ?? cwd;
        var repoName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoName))
        {
            repoName = "repository";
        }

        var filename = repoName + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ".tar.gz";
        var tarballPath = Path.Combine(Path.GetTempPath(), filename);
        var archive = await RunGitAsync(
            root,
            new[] { "archive", "--format=tar.gz", "-o", tarballPath, "HEAD" },
            cancellationToken).ConfigureAwait(false);
        if (!archive.Success)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = archive.Error ?? NullIfEmpty(archive.Stderr) ?? "Git archive failed.",
                ["stdout"] = archive.Stdout,
                ["stderr"] = archive.Stderr
            };
        }

        var commit = await RunGitAsync(root, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
        var remoteResult = await RunGitAsync(root, new[] { "remote", "-v" }, cancellationToken).ConfigureAwait(false);
        var remotes = new JObject();
        if (remoteResult.Success)
        {
            foreach (var rawLine in remoteResult.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length >= 2 && remotes[pieces[0]] is null)
                {
                    remotes[pieces[0]] = pieces[1];
                }
            }
        }

        var remoteArray = new JArray(remotes.Properties().Select(property => new JObject
        {
            ["name"] = property.Name,
            ["url"] = property.Value.Value<string>()
        }));
        var size = new FileInfo(tarballPath).Length;
        return new JObject
        {
            ["success"] = true,
            ["tarballPath"] = tarballPath,
            ["filePath"] = tarballPath,
            ["path"] = tarballPath,
            ["tarballFilename"] = filename,
            ["filename"] = filename,
            ["tarballSize"] = size,
            ["size"] = size,
            ["contentType"] = "application/gzip",
            ["repoName"] = repoName,
            ["gitRoot"] = root,
            ["branch"] = ToNullableValue(await CurrentBranchAsync(root, cancellationToken).ConfigureAwait(false)),
            ["commitSha"] = ToNullableValue(commit.Success ? NullIfEmpty(commit.Stdout) : null),
            ["remotes"] = remoteArray
        };
    }

    private static async Task<JObject> UploadWorktreeSnapshotAsync(
        JObject values,
        CancellationToken cancellationToken)
    {
        var uploadUrl = ExtractString(values, "uploadUrl", "url");
        var tarballPath = ExtractString(values, "tarballPath", "filePath", "path");
        if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(tarballPath))
        {
            return new JObject { ["success"] = false, ["error"] = "Missing uploadUrl or tarballPath" };
        }
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new JObject { ["success"] = false, ["error"] = "The snapshot upload URL must use HTTP or HTTPS." };
        }
        if (!File.Exists(tarballPath))
        {
            return new JObject { ["success"] = false, ["error"] = "The snapshot archive does not exist." };
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(ExtractString(values, "method") ?? "PUT"),
            uri);
        request.Content = new ByteArrayContent(File.ReadAllBytes(tarballPath!));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            ExtractString(values, "contentType") ?? "application/gzip");
        if (values["headers"] is JObject headers)
        {
            foreach (var property in headers.Properties())
            {
                var value = property.Value.Value<string>();
                if (string.IsNullOrWhiteSpace(value)
                    || request.Headers.TryAddWithoutValidation(property.Name, value))
                {
                    continue;
                }
                request.Content.Headers.TryAddWithoutValidation(property.Name, value);
            }
        }

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return new JObject
            {
                ["success"] = response.IsSuccessStatusCode,
                ["status"] = (int)response.StatusCode,
                ["statusText"] = response.ReasonPhrase ?? string.Empty,
                ["body"] = await response.Content.ReadAsStringAsync().ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private async Task<string?> ResolveGitRootAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var result = await RunGitAsync(
            directory,
            new[] { "rev-parse", "--show-toplevel" },
            cancellationToken).ConfigureAwait(false);
        var root = result.Success ? NullIfEmpty(result.Stdout) : null;
        return root is null ? null : Path.GetFullPath(root);
    }

    private async Task<string?> CurrentBranchAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(cwd, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, cancellationToken).ConfigureAwait(false);
        var branch = result.Success ? NullIfEmpty(result.Stdout) : null;
        return string.Equals(branch, "HEAD", StringComparison.Ordinal) ? null : branch;
    }

    private async Task<string?> DefaultBranchAsync(string cwd, CancellationToken cancellationToken)
    {
        var symbolic = await RunGitAsync(
            cwd,
            new[] { "symbolic-ref", "refs/remotes/origin/HEAD" },
            cancellationToken).ConfigureAwait(false);
        var reference = symbolic.Success ? NullIfEmpty(symbolic.Stdout) : null;
        const string prefix = "refs/remotes/origin/";
        if (reference?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            return reference.Substring(prefix.Length);
        }

        var current = await RunGitAsync(cwd, new[] { "branch", "--show-current" }, cancellationToken).ConfigureAwait(false);
        return current.Success ? NullIfEmpty(current.Stdout) ?? "main" : "main";
    }

    private async Task<string?> UpstreamBranchAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(cwd, new[] { "rev-parse", "--abbrev-ref", "@{upstream}" }, cancellationToken).ConfigureAwait(false);
        var branch = result.Success ? NullIfEmpty(result.Stdout) : null;
        return string.Equals(branch, "@{upstream}", StringComparison.Ordinal) ? null : branch;
    }

    private async Task<JObject> GetAheadBehindAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            cwd,
            new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new JObject { ["aheadCount"] = 0, ["behindCount"] = 0 };
        }

        var pieces = result.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var behind = pieces.Length > 0 && int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var behindValue)
            ? behindValue
            : 0;
        var ahead = pieces.Length > 1 && int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var aheadValue)
            ? aheadValue
            : 0;
        return new JObject { ["aheadCount"] = ahead, ["behindCount"] = behind };
    }

    private async Task<JObject> GetRecentBranchesAsync(string cwd, int limit, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            cwd,
            new[]
            {
                "for-each-ref",
                "--count=" + limit.ToString(CultureInfo.InvariantCulture),
                "--sort=-committerdate",
                "refs/heads",
                "--format=%(refname:short)"
            },
            cancellationToken).ConfigureAwait(false);
        var branches = result.Success
            ? result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        return new JObject { ["branches"] = new JArray(branches) };
    }

    private async Task<bool> BranchExistsAsync(string cwd, string branch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return false;
        }

        foreach (var candidate in new[] { branch, "refs/heads/" + branch, "refs/remotes/" + branch })
        {
            var result = await RunGitAsync(
                cwd,
                new[] { "rev-parse", "--verify", "--quiet", candidate },
                cancellationToken).ConfigureAwait(false);
            if (result.Success && NullIfEmpty(result.Stdout) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<JObject> GetStatusSummaryAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(cwd, new[] { "status", "--porcelain=v1", "-z" }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new JObject { ["type"] = "error" };
        }

        var staged = 0;
        var unstaged = 0;
        var untracked = 0;
        var entries = result.Stdout.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var indexStatus = entry.Length > 0 ? entry[0] : ' ';
            var worktreeStatus = entry.Length > 1 ? entry[1] : ' ';
            if (indexStatus == '?' && worktreeStatus == '?')
            {
                untracked++;
            }
            else
            {
                if (indexStatus != ' ')
                {
                    staged++;
                }
                if (worktreeStatus != ' ')
                {
                    unstaged++;
                }
            }

            if ((indexStatus == 'R' || indexStatus == 'C' || worktreeStatus == 'R' || worktreeStatus == 'C')
                && index + 1 < entries.Length)
            {
                index++;
            }
        }

        return new JObject
        {
            ["type"] = "success",
            ["stagedCount"] = staged,
            ["unstagedCount"] = unstaged,
            ["untrackedCount"] = untracked
        };
    }

    private async Task<JObject> GetIndexInfoAsync(string cwd, CancellationToken cancellationToken)
    {
        var root = await ResolveGitRootAsync(cwd, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return new JObject { ["lastModified"] = 0 };
        }

        var gitDirectoryResult = await RunGitAsync(root, new[] { "rev-parse", "--git-dir" }, cancellationToken).ConfigureAwait(false);
        var gitDirectory = gitDirectoryResult.Success ? NullIfEmpty(gitDirectoryResult.Stdout) : null;
        if (gitDirectory is null)
        {
            return new JObject { ["lastModified"] = 0 };
        }

        if (!Path.IsPathRooted(gitDirectory))
        {
            gitDirectory = Path.Combine(root, gitDirectory);
        }

        var indexPath = Path.Combine(gitDirectory, "index");
        var lastModified = File.Exists(indexPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(indexPath)).ToUnixTimeMilliseconds()
            : 0;
        return new JObject { ["lastModified"] = lastModified };
    }

    private static async Task<GitResult> RunGitAsync(
        string cwd,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return GitResult.Failed("The Git working directory does not exist: " + cwd);
        }

        var allArguments = new[] { "-C", cwd }.Concat(arguments).Select(QuoteArgument);
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", allArguments),
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return GitResult.Failed("Git could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = Task.Run(() => process.WaitForExit());
            using (cancellationToken.Register(() =>
                   {
                       try
                       {
                           if (!process.HasExited)
                           {
                               process.Kill();
                           }
                       }
                       catch
                       {
                       }
                   }))
            {
                await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new GitResult(process.ExitCode == 0, stdout, stderr, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GitResult.Failed(ex.Message);
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static string? ExtractString(JObject values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = values[key]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static List<string> ExtractStringArray(JToken? token)
    {
        if (token is not JArray values)
        {
            return new List<string>();
        }

        return values
            .Select(value => value.Type == JTokenType.String ? value.Value<string>() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static int ReadLimit(JObject values, int fallback, int maximum)
    {
        var value = values["limit"]?.Value<int?>();
        return value.HasValue ? Math.Max(1, Math.Min(value.Value, maximum)) : fallback;
    }

    private static JObject BuildCommandResult(GitResult result, string? branch)
    {
        var response = new JObject
        {
            ["success"] = result.Success,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr
        };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            response["branch"] = branch;
        }
        if (!result.Success)
        {
            response["error"] = result.Error ?? NullIfEmpty(result.Stderr) ?? "Git command failed.";
        }

        return response;
    }

    private static JObject BuildPatchResult(
        bool success,
        IEnumerable<string> appliedPaths,
        IEnumerable<string> skippedPaths,
        GitResult result)
    {
        var output = (result.Stdout + result.Stderr).Trim();
        return new JObject
        {
            ["success"] = success,
            ["appliedPaths"] = new JArray(appliedPaths),
            ["skippedPaths"] = new JArray(skippedPaths),
            ["conflictedPaths"] = new JArray(),
            ["execOutput"] = new JObject { ["output"] = output },
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["error"] = success
                ? JValue.CreateNull()
                : new JValue(result.Error ?? NullIfEmpty(result.Stderr) ?? "Git apply failed.")
        };
    }

    private static string[] ExtractPatchPaths(string patch)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in patch.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (rawLine.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                var path = rawLine.Substring("+++ b/".Length).Trim();
                if (path.Length > 0 && path != "/dev/null")
                {
                    paths.Add(path);
                }
                continue;
            }

            if (!rawLine.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                continue;
            }

            var marker = rawLine.IndexOf(" b/", StringComparison.Ordinal);
            if (marker >= 0 && marker + 3 < rawLine.Length)
            {
                paths.Add(rawLine.Substring(marker + 3).Trim());
            }
        }

        return paths.ToArray();
    }

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static JToken ToNullableValue(string? value)
    {
        return value is null ? JValue.CreateNull() : new JValue(value);
    }

    private static string TrimGitSuffix(string value)
    {
        var trimmed = value.TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(0, trimmed.Length - ".git".Length)
            : trimmed;
    }

    private sealed class NumstatEntry
    {
        public NumstatEntry(string path, string? previousPath, int? additions, int? deletions)
        {
            Path = path;
            PreviousPath = previousPath;
            Additions = additions;
            Deletions = deletions;
        }

        public string Path { get; }
        public string? PreviousPath { get; }
        public int? Additions { get; }
        public int? Deletions { get; }
    }

    private sealed class DiffStats
    {
        public DiffStats(int filesChanged, int linesAdded, int linesRemoved)
        {
            FilesChanged = filesChanged;
            LinesAdded = linesAdded;
            LinesRemoved = linesRemoved;
        }

        public int FilesChanged { get; }
        public int LinesAdded { get; }
        public int LinesRemoved { get; }
    }

    private sealed class GitResult
    {
        public GitResult(bool success, string stdout, string stderr, string? error)
        {
            Success = success;
            Stdout = stdout;
            Stderr = stderr;
            Error = error;
        }

        public bool Success { get; }
        public string Stdout { get; }
        public string Stderr { get; }
        public string? Error { get; }

        public static GitResult Failed(string error)
        {
            return new GitResult(false, string.Empty, string.Empty, error);
        }
    }
}

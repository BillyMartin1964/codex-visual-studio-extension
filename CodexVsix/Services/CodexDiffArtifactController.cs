using System;
using System.Collections.Generic;
using System.IO;

namespace CodexVsix.Services;

// Called on the UI thread so document checks and updates cannot race a tab close.
internal sealed class CodexDiffArtifactController
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly Func<string, string, bool> _updateOpenDocument;
    private readonly Action<string> _openDocument;

    internal CodexDiffArtifactController(string directory,
        Func<string, string, bool> updateOpenDocument, Action<string> openDocument)
    {
        _directory = directory;
        _updateOpenDocument = updateOpenDocument;
        _openDocument = openDocument;
    }

    internal void Show(string key, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        if (!_paths.TryGetValue(key, out var path) || !_updateOpenDocument(path, content!))
        {
            Directory.CreateDirectory(_directory);
            path = Path.Combine(_directory, "codex-" + Guid.NewGuid().ToString("N") + ".diff");
            File.WriteAllText(path, content);
            _paths[key] = path;
        }

        _openDocument(path);
    }

    internal void UpdateIfOpen(string key, string? content)
    {
        if (content is null || !_paths.TryGetValue(key, out var path)) return;
        _updateOpenDocument(path, content);
    }
}

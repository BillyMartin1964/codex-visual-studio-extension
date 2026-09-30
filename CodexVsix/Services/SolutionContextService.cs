using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CodexVsix.Services;

public sealed class SolutionContextService
{
    private const int MaxIndexedFiles = 100000;
    private static readonly TimeSpan FileIndexLifetime = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults"
    };

    private readonly object _fileIndexSync = new();
    private string _fileIndexRoot = string.Empty;
    private DateTime _fileIndexCreatedUtc;
    private IReadOnlyList<string> _fileIndex = Array.Empty<string>();
    private Task<IReadOnlyList<string>>? _fileIndexBuildTask;
    private string? _lastActiveDocumentPath;

    public string? TryGetBestWorkspaceDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetSolutionDirectory()
            ?? TryGetActiveProjectDirectory()
            ?? TryGetFirstProjectDirectory()
            ?? TryGetActiveDocumentDirectory();
    }

    public string? TryGetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var solutionPath = dte?.Solution?.FullName;
        if (!string.IsNullOrWhiteSpace(solutionPath) && File.Exists(solutionPath))
        {
            return Path.GetDirectoryName(solutionPath);
        }

        return null;
    }

    public string GetBestWorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetBestWorkspaceDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string BuildIdeContextSummary(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var sections = new List<string>();
        var solutionDirectory = TryGetSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            sections.Add("## Solution: " + FormatPath(workingDirectory, solutionDirectory));
        }

        var activeDocument = TryGetActiveDocumentPath();
        if (!string.IsNullOrWhiteSpace(activeDocument))
        {
            sections.Add("## Active file: " + FormatPath(workingDirectory, activeDocument));
        }

        var selectedItems = GetSelectedPaths(workingDirectory);
        if (selectedItems.Count > 0)
        {
            sections.Add("## Selected items:" + Environment.NewLine
                + string.Join(Environment.NewLine, selectedItems.Take(5).Select(path => "- " + path)));
        }

        var openDocuments = GetOpenDocumentPaths(workingDirectory);
        if (openDocuments.Count > 0)
        {
            sections.Add("## Open tabs:" + Environment.NewLine
                + string.Join(Environment.NewLine, openDocuments.Take(6).Select(path => "- " + path)));
        }

        var selectionSnippet = TryGetActiveSelectionSnippet();
        if (!string.IsNullOrWhiteSpace(selectionSnippet))
        {
            sections.Add("## Active selection of the file:" + Environment.NewLine + selectionSnippet);
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    public string? GetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetActiveDocumentPath();
    }

    public string GetActiveSelectionSnippetForPrompt(int maxLength = 6000)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetActiveSelectionSnippet(maxLength);
    }

    public IReadOnlyList<string> GetOpenDocumentPathsForIdeContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetOpenDocumentPathsRaw();
    }

    public string FormatPathForPrompt(string root, string path)
    {
        return FormatPath(root, path);
    }

    public IReadOnlyList<string> GetSelectedItemPathsForPrompt(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetSelectedPaths(workingDirectory);
    }

    public async Task<IReadOnlyList<string>> FindSolutionFilesAsync(string workingDirectory, string search, CancellationToken cancellationToken)
    {
        var root = NormalizeFullPath(workingDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var normalized = (search ?? string.Empty).Trim().Replace('\\', '/');
        var files = await GetFileIndexAsync(root, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return files
            .Where(f => string.IsNullOrWhiteSpace(normalized) || f.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(f => Score(f, normalized))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    public void InvalidateFileIndex()
    {
        lock (_fileIndexSync)
        {
            _fileIndexRoot = string.Empty;
            _fileIndexCreatedUtc = DateTime.MinValue;
            _fileIndex = Array.Empty<string>();
            _fileIndexBuildTask = null;
        }
    }

    public IReadOnlyList<string> GetSolutionFilePaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution?.Projects is null)
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Project project in dte.Solution.Projects)
            {
                CollectProjectFiles(project, files, visitedProjects);
            }
        }
        catch
        {
        }

        return files
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetCodexConfigPath()
    {
        return Path.Combine(GetCodexHomeDirectory(), "config.toml");
    }

    public string GetCodexHomeDirectory()
    {
        return CodexEnvironmentPathHelper.GetCodexHomeDirectory();
    }

    public string GetCodexSkillsDirectory()
    {
        return Path.Combine(GetCodexHomeDirectory(), "skills");
    }

    public void OpenCodexConfig()
    {
        var path = GetCodexConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenCodexSkillsDirectory()
    {
        var path = GetCodexSkillsDirectory();
        Directory.CreateDirectory(path);
        OpenPath(path);
    }

    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenFileInVisualStudio(string path, int? line = null, int? column = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!TryOpenDocumentInVisualStudio(path))
        {
            return;
        }

        if (line is > 0)
        {
            NavigateActiveDocument(line.Value, column);
        }
    }

    private static bool TryOpenDocumentInVisualStudio(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryOpenDocumentWithDte(path))
        {
            return true;
        }

        try
        {
            VsShellUtilities.OpenDocument(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider, path);
            return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryOpenDocumentWithDte(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ItemOperations is null)
            {
                return false;
            }

            var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            window?.Activate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NavigateActiveDocument(int line, int? column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var selection = dte?.ActiveDocument?.Selection as TextSelection;
            if (selection is null)
            {
                return;
            }

            if (column is > 0)
            {
                selection.MoveToLineAndOffset(line, column.Value, false);
            }
            else
            {
                selection.GotoLine(line, false);
            }
        }
        catch
        {
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public string CreateSkillTemplate(string skillName, string description)
    {
        var normalizedSkillName = NormalizeSkillName(skillName);
        var skillDirectory = Path.Combine(GetCodexSkillsDirectory(), normalizedSkillName);
        Directory.CreateDirectory(skillDirectory);

        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            File.WriteAllText(
                skillFile,
                BuildSkillTemplate(normalizedSkillName, description),
                new UTF8Encoding(false));
        }

        return skillFile;
    }

    public static bool IsValidSkillName(string? skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return false;
        }

        var trimmed = skillName!.Trim();
        if (!char.IsLetterOrDigit(trimmed[0]))
        {
            return false;
        }

        return trimmed.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
    }

    private static string NormalizeSkillName(string skillName)
    {
        var trimmed = (skillName ?? string.Empty).Trim();
        if (!IsValidSkillName(trimmed))
        {
            throw new ArgumentException(new LocalizationService().InvalidSkillNameMessage, nameof(skillName));
        }

        return trimmed;
    }

    private static string BuildSkillTemplate(string skillName, string description)
    {
        var localization = new LocalizationService();
        var summary = string.IsNullOrWhiteSpace(description)
            ? localization.SkillTemplateSummary
            : description.Trim();

        return "# " + skillName + Environment.NewLine
            + Environment.NewLine
            + summary + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateWhenToUseHeading + Environment.NewLine
            + localization.SkillTemplateWhenToUseBullet + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateFlowHeading + Environment.NewLine
            + localization.SkillTemplateFlowStep1 + Environment.NewLine
            + localization.SkillTemplateFlowStep2 + Environment.NewLine
            + localization.SkillTemplateFlowStep3 + Environment.NewLine;
    }

    private async Task<IReadOnlyList<string>> GetFileIndexAsync(string root, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<string>> buildTask;
        lock (_fileIndexSync)
        {
            if (string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _fileIndexCreatedUtc < FileIndexLifetime
                && _fileIndex.Count > 0)
            {
                return _fileIndex;
            }

            if (_fileIndexBuildTask is null || !string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _fileIndexRoot = root;
                _fileIndexBuildTask = Task.Run(() => BuildFileIndex(root));
            }

            buildTask = _fileIndexBuildTask;
        }

        IReadOnlyList<string> result;
        try
        {
            result = await buildTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_fileIndexSync)
            {
                if (ReferenceEquals(_fileIndexBuildTask, buildTask))
                {
                    _fileIndexBuildTask = null;
                }
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_fileIndexSync)
        {
            if (ReferenceEquals(_fileIndexBuildTask, buildTask)
                && string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _fileIndex = result;
                _fileIndexCreatedUtc = DateTime.UtcNow;
                _fileIndexBuildTask = null;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> BuildFileIndex(string root)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0 && files.Count < MaxIndexedFiles)
        {
            var directory = directories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    files.Add(MakeRelative(root, file));
                    if (files.Count >= MaxIndexedFiles)
                    {
                        break;
                    }
                }

                if (files.Count >= MaxIndexedFiles)
                {
                    break;
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IgnoredDirectoryNames.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    directories.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                || ex is IOException
                || ex is PathTooLongException
                || ex is DirectoryNotFoundException)
            {
            }
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryGetActiveProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ActiveSolutionProjects is not Array activeProjects)
            {
                return null;
            }

            foreach (var entry in activeProjects)
            {
                if (entry is Project project)
                {
                    var directory = TryGetProjectDirectory(project);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetFirstProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var projects = dte?.Solution?.Projects;
            if (projects is null)
            {
                return null;
            }

            foreach (Project project in projects)
            {
                var directory = TryGetProjectDirectory(project);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private string? TryGetActiveDocumentDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = TryGetActiveDocumentPath();
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
    }

    internal bool TryUpdateOpenTextDocument(string path, string content)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents is null) return false;
            foreach (Document document in dte.Documents)
            {
                if (!string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase)) continue;
                // Preserve unsaved edits made manually to the generated artifact.
                if (!document.Saved || document.Object("TextDocument") is not TextDocument text) return false;
                var start = text.StartPoint.CreateEditPoint();
                if (!string.Equals(start.GetText(text.EndPoint), content, StringComparison.Ordinal))
                {
                    start.ReplaceText(text.EndPoint, content, 0);
                    document.Save();
                }

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private string? TryGetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var path = TryGetDteActiveDocumentPath() ?? TryGetActiveTextViewDocumentPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _lastActiveDocumentPath = path;
            return path;
        }

        if (!string.IsNullOrWhiteSpace(_lastActiveDocumentPath) && File.Exists(_lastActiveDocumentPath))
        {
            return _lastActiveDocumentPath;
        }

        _lastActiveDocumentPath = null;
        return null;
    }

    private static string? TryGetDteActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            return NormalizeExistingDocumentPath(dte?.ActiveDocument?.FullName);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetActiveTextViewDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!TryGetActiveTextView(out var textView)
                || ErrorHandler.Failed(textView!.GetBuffer(out var textLines))
                || textLines is not IPersistFileFormat persistFileFormat
                || ErrorHandler.Failed(persistFileFormat.GetCurFile(out var filePath, out _)))
            {
                return null;
            }

            return NormalizeExistingDocumentPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeExistingDocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path!);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetActiveTextView(out IVsTextView? textView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        textView = null;

        var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
        return textManager is not null
            && ErrorHandler.Succeeded(textManager.GetActiveView(0, null, out textView))
            && textView is not null;
    }

    private static string? TryGetProjectDirectory(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            return null;
        }

        try
        {
            var fullName = project.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                if (File.Exists(fullName))
                {
                    return Path.GetDirectoryName(fullName);
                }

                if (Directory.Exists(fullName))
                {
                    return fullName;
                }
            }
        }
        catch
        {
        }

        try
        {
            var fullPath = project.Properties?.Item("FullPath")?.Value as string;
            if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return null;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                var nested = TryGetProjectDirectory(item.SubProject);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void CollectProjectFiles(Project? project, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
        {
            return;
        }

        var projectKey = TryGetProjectKey(project);
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            var key = projectKey!;
            if (!visitedProjects.Add(key))
            {
                return;
            }
        }

        try
        {
            TryAddFile(files, project.FullName);
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                CollectProjectItemFiles(item, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static void CollectProjectItemFiles(ProjectItem item, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            for (short index = 1; index <= item.FileCount; index++)
            {
                TryAddFile(files, item.FileNames[index]);
            }
        }
        catch
        {
        }

        try
        {
            CollectProjectFiles(item.SubProject, files, visitedProjects);
        }
        catch
        {
        }

        try
        {
            if (item.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem child in item.ProjectItems)
            {
                CollectProjectItemFiles(child, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static string? TryGetProjectKey(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!string.IsNullOrWhiteSpace(project.UniqueName))
            {
                return project.UniqueName;
            }
        }
        catch
        {
        }

        try
        {
            return project.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void TryAddFile(ICollection<string> files, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var filePath = path!;
        if (File.Exists(filePath))
        {
            files.Add(filePath);
        }
    }

    private static string MakeRelative(string root, string file)
    {
        var normalizedRoot = NormalizeFullPath(root);
        var normalizedFile = NormalizeFullPath(file);
        var rootWithSeparator = EnsureTrailingDirectorySeparator(normalizedRoot);
        if (!normalizedFile.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedFile.Replace('\\', '/');
        }

        return normalizedFile.Substring(rootWithSeparator.Length).Replace('\\', '/');
    }

    private static string FormatPath(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalizedPath = NormalizeFullPath(path);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var normalizedRoot = NormalizeFullPath(root);
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            if (normalizedPath.StartsWith(EnsureTrailingDirectorySeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase))
            {
                return MakeRelative(normalizedRoot, normalizedPath);
            }
        }

        return normalizedPath.Replace('\\', '/');
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(path!.Trim());
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? root!
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path!.Trim();
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }

    private static IReadOnlyList<string> GetOpenDocumentPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetOpenDocumentPathsRaw()
            .Select(path => FormatPath(workingDirectory, path))
            .ToList();
    }

    private static IReadOnlyList<string> GetOpenDocumentPathsRaw()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents is null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (Document document in dte.Documents)
            {
                var path = NormalizeExistingDocumentPath(document.FullName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path!);
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> GetSelectedPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.SelectedItems is null)
            {
                return Array.Empty<string>();
            }

            var items = new List<string>();
            foreach (SelectedItem selectedItem in dte.SelectedItems)
            {
                var path = selectedItem.ProjectItem?.FileNames[1]
                    ?? selectedItem.Project?.FullName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                items.Add(FormatPath(workingDirectory, path));
            }

            return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string TryGetActiveSelectionSnippet(int maxLength = 900)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var textSelection = dte?.ActiveDocument?.Selection as TextSelection;
            var selectedText = textSelection?.Text;
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                return NormalizeSelectionSnippet(selectedText!, maxLength);
            }
        }
        catch
        {
        }

        try
        {
            if (!TryGetActiveTextView(out var textView)
                || ErrorHandler.Failed(textView!.GetSelection(
                    out var startLine,
                    out var startIndex,
                    out var endLine,
                    out var endIndex))
                || (startLine == endLine && startIndex == endIndex)
                || ErrorHandler.Failed(textView.GetBuffer(out var textLines)))
            {
                return string.Empty;
            }

            NormalizeSelectionRange(ref startLine, ref startIndex, ref endLine, ref endIndex);
            return ErrorHandler.Succeeded(textLines.GetLineText(
                startLine,
                startIndex,
                endLine,
                endIndex,
                out var selectedText))
                ? NormalizeSelectionSnippet(selectedText, maxLength)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void NormalizeSelectionRange(
        ref int startLine,
        ref int startIndex,
        ref int endLine,
        ref int endIndex)
    {
        if (startLine < endLine || (startLine == endLine && startIndex <= endIndex))
        {
            return;
        }

        (startLine, endLine) = (endLine, startLine);
        (startIndex, endIndex) = (endIndex, startIndex);
    }

    private static string NormalizeSelectionSnippet(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        var normalized = text!.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, maxLength).TrimEnd() + Environment.NewLine + "...";
    }

    private static int Score(string file, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return int.MaxValue / 2;
        }

        var idx = file.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? int.MaxValue : idx * 10 + file.Length;
    }
}

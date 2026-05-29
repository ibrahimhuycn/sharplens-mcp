using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;

namespace SharpLensMcp;

// Razor file processing: compiles .razor to C#, manages virtual documents in the
// Roslyn workspace, and caches results. Called lazily on first access.
public partial class RoslynService
{
    private readonly Dictionary<string, RazorFileInfo> _razorDocuments = new();
    private readonly ConcurrentDictionary<string, RazorProjectEngine> _engineCache = new();
    private readonly string _razorVirtualDir = "obj/SharpLensMcp/RazorGenerated";

    /// <summary>
    /// Detect Razor projects by checking for Blazor component references.
    /// </summary>
    internal static bool IsRazorProject(Project project)
    {
        // Check metadata references — Blazor projects reference Components, Razor, etc.
        if (project.MetadataReferences.Any(r =>
        {
            var display = r.Display ?? "";
            return display.Contains("Microsoft.AspNetCore.Components", StringComparison.Ordinal) ||
                   display.Contains("Microsoft.AspNetCore.Razor", StringComparison.Ordinal);
        }))
            return true;

        // Fallback: check if the project directory actually contains .razor files.
        // This mirrors how VS/VS Code detects Razor projects — by looking at the
        // project SDK (Microsoft.NET.Sdk.Razor) or by scanning the file system.
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir != null && Directory.Exists(projectDir) &&
            Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories).Any())
            return true;

        return false;
    }

    /// <summary>
    /// Scan project directory for .razor files and register them for lazy processing.
    /// Called at the end of LoadSolutionAsync.
    /// </summary>
    internal void DiscoverRazorFiles()
    {
        if (_solution == null) return;

        foreach (var project in _solution.Projects)
        {
            if (!IsRazorProject(project)) continue;

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (projectDir == null) continue;

            foreach (var razorPath in Directory.EnumerateFiles(
                projectDir, "*.razor", SearchOption.AllDirectories))
            {
                var key = NormalizeRazorPath(razorPath);
                if (!_razorDocuments.ContainsKey(key))
                    _razorDocuments[key] = null!; // marker: discovered, not processed
            }
        }
    }

    /// <summary>
    /// Get or lazily process a .razor file. Returns null if not found.
    /// </summary>
    internal RazorFileInfo? GetRazorFileInfo(string razorFilePath)
    {
        var key = NormalizeRazorPath(razorFilePath);

        // Reconstruct absolute path: try stored solution dir, then each project's dir.
        // MSBuildWorkspace may set _solution.FilePath to a different directory.
        var absPath = Path.IsPathRooted(key) ? key : ResolveAbsolutePath(key);

        if (!_razorDocuments.TryGetValue(key, out var info))
        {
            var project = FindProjectForFile(absPath) ?? _solution?.Projects.FirstOrDefault();
            _razorDocuments[key] = null!;
            info = ProcessRazorFile(absPath, project);
            return info;
        }

        if (info == null)
        {
            var project = FindProjectForFile(absPath) ?? _solution?.Projects.FirstOrDefault();
            info = ProcessRazorFile(absPath, project);
        }

        if (info == null)
        {
            // Lazy first-access processing — discovered but not yet processed
            var project = FindProjectForFile(absPath);
            if (project == null) return null;

            info = ProcessRazorFile(absPath, project);
        }

        return info;
    }

    /// <summary>
    /// Process a single .razor file: compile to C#, add virtual document to workspace.
    /// </summary>
    internal RazorFileInfo ProcessRazorFile(string razorAbsPath, Project project)
    {
        var razorSource = File.ReadAllText(razorAbsPath);

        var engine = GetOrCreateRazorEngine(project, razorAbsPath);
        var projectItem = engine.FileSystem.GetItem(razorAbsPath)!;
        var codeDoc = engine.Process(projectItem);
        var csharpDoc = codeDoc.GetCSharpDocument();

        var info = new RazorFileInfo
        {
            RazorFilePath = NormalizeRazorPath(razorAbsPath),
            RazorAbsPath = razorAbsPath,
            VirtualDocumentId = default!,
            RazorSourceText = razorSource,
            GeneratedSourceText = csharpDoc.GeneratedCode!,
            CSharpDocument = csharpDoc!,
            ProcessedAt = DateTime.UtcNow
        };

        // Add virtual C# document to solution
        var docId = AddVirtualRazorDocument(project, info);
        info = info with { VirtualDocumentId = docId };

        // Cache
        var key = NormalizeRazorPath(razorAbsPath);
        _razorDocuments[key] = info;

        return info;
    }

    /// <summary>
    /// Create a RazorProjectEngine for a specific .razor file, computing the
    /// correct namespace (project root + folder path — matches the Razor SDK).
    /// VS2026/Rider do the same: namespace is derived from project root namespace
    /// and the file's path relative to the project directory.
    /// </summary>
    private RazorProjectEngine GetOrCreateRazorEngine(Project project, string razorAbsPath)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath ?? "/tmp") ?? "/tmp";
        var rootNamespace = project.DefaultNamespace ?? project.Name;

        // Compute namespace from project root + subfolder path
        string ns = rootNamespace;
        if (razorAbsPath.StartsWith(projectDir, PathComparison))
        {
            var relativePath = razorAbsPath[(projectDir.Length + 1)..];
            var subDir = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrEmpty(subDir))
            {
                var nsSuffix = subDir.Replace(Path.DirectorySeparatorChar, '.')
                                    .Replace(Path.AltDirectorySeparatorChar, '.');
                ns = rootNamespace + "." + nsSuffix;
            }
        }

        var cacheKey = projectDir + "|" + ns;
        if (_engineCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var fileSystem = RazorProjectFileSystem.Create(projectDir);

        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            builder => builder.SetNamespace(ns));

        _engineCache[cacheKey] = engine;
        return engine;
    }

    /// <summary>
    /// Add a synthetic C# document to the solution for the generated razor code.
    /// Uses a path under obj/ to avoid conflicts with real files.
    /// </summary>
    private DocumentId AddVirtualRazorDocument(Project project, RazorFileInfo info)
    {
        var fileName = Path.GetFileNameWithoutExtension(info.RazorFilePath) + "_razor.g.cs";
        var relativeDir = Path.GetDirectoryName(info.RazorFilePath)?.Replace('\\', '/');

        var projectDir = Path.GetDirectoryName(project.FilePath ?? Path.GetTempPath()) ?? Path.GetTempPath();
        var virtualPath = Path.Combine(projectDir, _razorVirtualDir,
            relativeDir ?? "", fileName);

        var docId = DocumentId.CreateNewId(project.Id);
        var sourceText = SourceText.From(info.GeneratedSourceText);
        var folders = (relativeDir ?? "").Split('/',
            StringSplitOptions.RemoveEmptyEntries);

        _solution = _solution!.AddDocument(
            docId, fileName, sourceText, folders, virtualPath);

        return docId;
    }

    /// <summary>
    /// Remove a virtual razor document from the solution.
    /// </summary>
    internal void RemoveVirtualRazorDocument(string razorFilePath)
    {
        var key = NormalizeRazorPath(razorFilePath);
        if (_razorDocuments.TryGetValue(key, out var info) && info != null)
        {
            _solution = _solution!.RemoveDocument(info.VirtualDocumentId);
        }
        _razorDocuments.Remove(key);
    }

    /// <summary>
    /// Check if a document is a razor-generated virtual document.
    /// </summary>
    internal bool IsRazorGeneratedDocument(Document document) =>
        _razorDocuments.Values.Any(r => r != null && r.VirtualDocumentId.Equals(document.Id));

    /// <summary>
    /// Check if a DocumentId belongs to a razor-generated virtual document.
    /// </summary>
    internal bool IsRazorGeneratedDocument(DocumentId docId) =>
        _razorDocuments.Values.Any(r => r != null && r.VirtualDocumentId.Equals(docId));

    /// <summary>
    /// Eagerly process all undiscovered razor files so their virtual C# documents
    /// are in the solution. Required before cross-file operations like rename_symbol
    /// so Roslyn can find references across the entire solution.
    /// </summary>
    internal void EnsureAllRazorFilesLoaded()
    {
        foreach (var kvp in _razorDocuments.ToList())
        {
            if (kvp.Value != null) continue;
            // Lazy-load triggers processing
            GetRazorFileInfo(kvp.Key);
        }
    }

    /// <summary>
    /// Normalize a razor file path: convert to solution-relative with forward slashes.
    /// </summary>
    private string NormalizeRazorPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        var solutionDir = _loadedSolutionDir ?? (_solution?.FilePath != null ? Path.GetDirectoryName(_solution.FilePath)?.Replace('\\', '/') : null);
        if (solutionDir != null)
        {
            solutionDir = solutionDir.Replace('\\', '/');
            if (normalized.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[(solutionDir.Length + 1)..];
        }
        return normalized;
    }

    /// <summary>
    /// Clear all razor state — document registry, virtual docs, engine cache.
    /// Called before loading a new solution.
    /// </summary>
    internal void ClearRazorState()
    {
        _razorDocuments.Clear();
        _engineCache.Clear();
    }

    /// <summary>
    /// Convert a solution-relative key to an absolute path by trying the stored
    /// solution directory, then each project's directory.
    /// </summary>
    private string ResolveAbsolutePath(string key)
    {
        if (Path.IsPathRooted(key)) return key;

        // First: stored solution directory
        if (_loadedSolutionDir != null)
        {
            var candidate = Path.GetFullPath(Path.Combine(_loadedSolutionDir, key));
            if (File.Exists(candidate)) return candidate;
        }

        // Second: try _solution.FilePath → directory
        if (_solution?.FilePath != null)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(_solution.FilePath)!, key));
            if (File.Exists(candidate)) return candidate;
        }

        // Third: try each project's directory
        if (_solution != null)
            foreach (var project in _solution.Projects)
            {
                var dir = Path.GetDirectoryName(project.FilePath);
                if (dir == null) continue;
                var candidate = Path.GetFullPath(Path.Combine(dir, key));
                if (File.Exists(candidate)) return candidate;
            }

        // Fallback: current directory
        return Path.GetFullPath(key);
    }
}

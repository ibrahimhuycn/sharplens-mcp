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

        // Compute absolute path early — FindProjectForFile needs it
        var absPath = _solution?.FilePath != null
            ? Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(_solution.FilePath)!, key))
            : Path.GetFullPath(key);

        if (!_razorDocuments.TryGetValue(key, out var info))
        {
            // Not yet discovered — auto-discover on first access (VS/VS Code behavior)
            // so that tools don't require prior loading of every .razor file
            if (!File.Exists(absPath)) return null;

            var project = FindProjectForFile(absPath)
                // Fallback: if project has no FilePath (common in test/adhoc workspaces),
                // try any available project — the virtual document just needs a Roslyn
                // project to attach to.
                ?? _solution?.Projects.FirstOrDefault();

            if (project == null) return null;

            // Register for future access and process immediately
            _razorDocuments[key] = null!;
            info = ProcessRazorFile(absPath, project);
            return info;
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

        var engine = GetOrCreateRazorEngine(project);
        var projectItem = engine.FileSystem.GetItem(razorAbsPath)!;
        var codeDoc = engine.Process(projectItem);
        var csharpDoc = codeDoc.GetCSharpDocument();

        var info = new RazorFileInfo
        {
            RazorFilePath = NormalizeRazorPath(razorAbsPath),
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
    /// Create a RazorProjectEngine for the given project, cached per project directory.
    /// </summary>
    private RazorProjectEngine GetOrCreateRazorEngine(Project project)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath ?? "/tmp") ?? "/tmp";
        if (_engineCache.TryGetValue(projectDir, out var cached))
            return cached;

        var fileSystem = RazorProjectFileSystem.Create(projectDir);
        var rootNamespace = project.DefaultNamespace ?? project.Name;

        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            builder => builder.SetNamespace(rootNamespace));

        _engineCache[projectDir] = engine;
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
        _razorDocuments.Values.Any(r => r != null && r.VirtualDocumentId == document.Id);

    /// <summary>
    /// Check if a DocumentId belongs to a razor-generated virtual document.
    /// </summary>
    internal bool IsRazorGeneratedDocument(DocumentId docId) =>
        _razorDocuments.Values.Any(r => r != null && r.VirtualDocumentId == docId);

    /// <summary>
    /// Normalize a razor file path: convert to solution-relative with forward slashes.
    /// </summary>
    private string NormalizeRazorPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (_solution?.FilePath != null)
        {
            var solutionDir = Path.GetDirectoryName(_solution.FilePath)!
                .Replace('\\', '/');
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
}

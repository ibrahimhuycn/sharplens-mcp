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
        return project.MetadataReferences.Any(r =>
            (r.Display ?? "").Contains("Microsoft.AspNetCore.Components"));
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

        if (!_razorDocuments.TryGetValue(key, out var info))
            return null;

        if (info == null)
        {
            // Lazy first-access processing
            var project = FindProjectForFile(key);
            if (project == null) return null;

            var absPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(_solution!.FilePath)!, key));
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

# Razor (.razor) + Filesystem Watcher — Comprehensive Implementation Plan

> **Revision 2** — Uses `Microsoft.CodeAnalysis.Razor` instead of building position mapping from scratch. Total effort: **5-7 weeks** (2 devs) or **8-10 weeks** (1 dev).

---

## Parallel Workstreams

```
                     WEEK 1          WEEK 2          WEEK 3          WEEK 4          WEEK 5          WEEK 6          WEEK 7

Dev 1:  ───[Phase 0: Spike]────[A: Watcher]──────────────[B: Core Infra]────────────────[C1: Nav]──[D: Refactoring]────────────────────[G: Testing + Ship]──
Dev 2:  ───[Phase 0: Spike]────[Phase 0 continued]───────[B: Core Infra]────────────────[C2: Analysis]──────[E: Type + Disc + Actions]────[F: Sync + Tag]────[G: Testing]──

                     │ Phase 0      │ A (4d)         │ B (6d)         │ C1+C2 (2d)   │ D (4d) + E (5d)│ F (2d)         │ G (6d)        │
                     │ (3d both)    │                │ (both)         │              │                │                │               │
                     ▼              ▼                ▼                ▼              ▼                ▼                ▼               ▼
Gates:          [Spike PASS/FAIL] [Watcher SHIPS!] [Infra READY] [Tools WORKING] [Refactor DONE] [Sync + Tags DONE] [ALL TESTS PASS]
```

**Key parallelization decisions:**

1. **Phase 0 (Spike):** Both devs. One writes the spike code, the other audits the `Microsoft.CodeAnalysis.Razor` assembly for usable public APIs.

2. **Stream A (Watcher) vs Stream B (Core):** If Dev 1 finishes the spike early, they can start the Watcher while Dev 2 continues spike work (tag helper API audit, performance testing). The Watcher is completely independent — no Razor dependency.

3. **Streams C1 + C2 (Navigation + Analysis):** Run simultaneously. Dev 1 adapts 9 navigation tools, Dev 2 adapts 6 analysis tools. Both use the shared `PrepareRazorAwareContext` helper built in Phase B. These streams take only 2 days each because position translation is now a library call.

4. **Stream D (Refactoring) + Stream E (Type/Discovery):** Dev 1 takes refactoring (the hardest stream, 4 days). Dev 2 takes the remaining tools (type, discovery, compound, code actions — simpler, 5 days). These run in parallel with zero overlap.

5. **Stream F (sync_documents + Tag Helpers):** Both devs. Dev 1 extends sync_documents; Dev 2 adds tag helper support. 2 days.

6. **Stream G (Testing):** Both devs. Integration tests, edge cases, docs. 6 days.

---

## Phase 0: Feasibility Spike (Week 1, Days 1-3)

### Both Developers — REQUIRED Gate

**This phase determines whether the entire approach is viable.** No parallel work on Razor begins until Phase 0 passes.

### Task 0.1 — Razor Pipeline Spike
**Owner: Dev 1 | 1.5 days**

Write a standalone spike that proves the approach:

```csharp
// Tests/Spike/RazorPipelineSpike.cs — throwaway code
[Fact]
public async Task Spike_ProcessRazorAndQuerySymbols()
{
    // 1. Create a sample .razor file in memory
    var razorSource = @"
@code {
    private int _counter;
    
    private void IncrementCount()
    {
        _counter++;
    }
}";

    // 2. Set up AdhocWorkspace with Blazor references
    var workspace = new AdhocWorkspace();
    var project = workspace.AddProject("TestProject", LanguageNames.CSharp);
    project = project.AddMetadataReference(
        MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Components.ComponentBase).Assembly.Location));
    
    // 3. Process .razor via RazorProjectEngine
    var engine = RazorProjectEngine.Create(
        RazorConfiguration.Default,
        RazorProjectFileSystem.Create("/tmp/test"),
        builder => builder.SetNamespace("TestApp"));
    
    var codeDoc = engine.Process(RazorSourceDocument.Create(razorSource, "Counter.razor"));
    var csharpDoc = codeDoc.GetCSharpDocument();
    
    // 4. Verify C# compiles
    var generatedCSharp = csharpDoc.GeneratedCode;
    var tree = CSharpSyntaxTree.ParseText(generatedCSharp);
    project = project.AddDocument("Counter_razor.g.cs", tree.GetText()).Project;
    var compilation = await project.GetCompilationAsync();
    var diagnostics = compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error);
    diagnostics.Should().BeEmpty("generated C# must compile without errors");
    
    // 5. Verify source mappings
    // "_counter" is at some position in .razor
    var counterPosition = FindInSource(razorSource, "_counter");
    var mapping = csharpDoc.SourceMappings
        .FirstOrDefault(m => counterPosition >= m.OriginalSpan.AbsoluteIndex
                          && counterPosition < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);
    mapping.Should().NotBeNull("_counter must have a source mapping");
    
    // 6. Round-trip test
    var fraction = (double)(counterPosition - mapping.OriginalSpan.AbsoluteIndex) 
                 / mapping.OriginalSpan.Length;
    var generatedOffset = mapping.GeneratedSpan.AbsoluteIndex 
                        + (int)(fraction * mapping.GeneratedSpan.Length);
    // Reverse map should give back original position (±2 chars)
    
    // 7. Run FindReferences on the symbol
    var semanticModel = compilation.GetSemanticModel(tree);
    var root = await tree.GetRootAsync();
    var node = root.FindToken(generatedOffset).Parent;
    var symbol = semanticModel.GetDeclaredSymbol(node);
    symbol.Should().NotBeNull();
    symbol!.Name.Should().Be("_counter");
}
```

**Spike checklist:**
- [ ] `Microsoft.CodeAnalysis.Razor` NuGet resolves and compiles with net8.0 target
- [ ] `RazorProjectEngine.Process()` produces valid C# for `@code`, `@bind`, `@onclick`, `@foreach`, `@typeparam`
- [ ] Generated C# compiles with real Blazor references (ComponentBase)
- [ ] `SourceMappings` give character-accurate positions for code block content
- [ ] Round-trip mapping test: `.razor` → C# → `.razor` returns original position (±2 chars)
- [ ] FindReferences on a generated C# symbol returns results
- [ ] Processing 50 `.razor` files takes < 3 seconds

### Task 0.2 — API Surface Audit
**Owner: Dev 2 (parallel with 0.1) | 1.5 days**

```csharp
// Reflection audit of Microsoft.CodeAnalysis.Razor
var assembly = typeof(/* any type from Microsoft.CodeAnalysis.Razor */).Assembly;
var exportedTypes = assembly.GetExportedTypes();

foreach (var type in exportedTypes.OrderBy(t => t.FullName))
{
    Console.WriteLine($"{type.FullName}");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
```

**Audit questions to answer:**
1. Is `RazorDocumentMappingService` or equivalent a public type? What mapping methods does it expose?
2. Is there a `RazorProjectService` or equivalent for document lifecycle management?
3. What `TagHelperDescriptor` discovery APIs exist?
4. What workspace integration types exist (e.g., `RazorWorkspaceListener`)?
5. Which types require `Microsoft.CodeAnalysis.Workspaces` (already referenced) vs. which use the LSP model?

**Output:** A table of available services, what we can use directly, and what we must build ourselves.

### Task 0.3 — Decision Gate
**Both devs | End of Day 3**

Review spike results. Decision points:

| Question | PASS if... | FAIL if... | Fallback |
|---|---|---|---|
| Source mappings accurate? | Round-trip returns position within ±2 chars for `@code` content | Mappings are missing or wildly wrong for basic C# | Use `Microsoft.AspNetCore.Razor.Language` + custom mapping (adds ~2 weeks) |
| Package compatible? | Compiles and runs on net8.0 | Version conflicts or missing APIs | Try different package version; if all fail, custom mapping |
| Tag helper API usable? | Public API exists for discovery | Discovery API is internal | Basic tag helpers (scan known assemblies) is sufficient for launch |
| Performance acceptable? | 50 files in < 3s | > 10s for 50 files | Lazy processing + caching; only process files when first queried |

---

## Stream A: Filesystem Watcher (Week 1 Day 4 → Week 2 Day 3)

### Can start immediately after Phase 0 spike passes. No dependency on Razor.

**Owner: Dev 1 | 4 days**

### A.1 — Core Implementation (2 days)

```csharp
// src/RoslynService.Watcher.cs
public partial class RoslynService
{
    private FileSystemWatcher? _fileWatcher;
    private System.Timers.Timer? _debounceTimer;
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchLock = new();

    internal void StartFileWatcher()
    {
        var enabled = Environment.GetEnvironmentVariable("SHARPLENS_WATCH_MODE");
        if (enabled?.ToLower() != "true" || _solution?.FilePath == null)
            return;

        var solutionDir = Path.GetDirectoryName(_solution.FilePath);
        if (solutionDir == null) return;

        var debounceMs = int.TryParse(
            Environment.GetEnvironmentVariable("SHARPLENS_WATCH_DEBOUNCE_MS"), 
            out var ms) ? ms : 300;

        var extensions = Environment.GetEnvironmentVariable("SHARPLENS_WATCH_EXTENSIONS") 
                         ?? ".cs,.razor,.cshtml";

        _fileWatcher = new FileSystemWatcher(solutionDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite 
                         | NotifyFilters.FileName 
                         | NotifyFilters.CreationTime,
        };

        foreach (var ext in extensions.Split(',').Select(e => e.Trim()))
            _fileWatcher.Filters.Add($"*{ext}");

        _debounceTimer = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounceTimer.Elapsed += OnDebounceElapsed;

        // Wire events
        _fileWatcher.Changed += (s, e) => { lock(_watchLock) { _pendingPaths.Add(e.FullPath); _debounceTimer.Stop(); _debounceTimer.Start(); } };
        _fileWatcher.Created += (s, e) => { lock(_watchLock) { _pendingPaths.Add(e.FullPath); _debounceTimer.Stop(); _debounceTimer.Start(); } };
        _fileWatcher.Deleted += (s, e) => { lock(_watchLock) { _pendingPaths.Add(e.FullPath); _debounceTimer.Stop(); _debounceTimer.Start(); } };
        _fileWatcher.Renamed += (s, e) => { lock(_watchLock) { _pendingPaths.Add(e.OldFullPath); _pendingPaths.Add(e.FullPath); _debounceTimer.Stop(); _debounceTimer.Start(); } };
        _fileWatcher.Error += (s, e) => { Console.Error.WriteLine($"[Watcher] Error: {e.GetException().Message}"); /* restart logic */ };
        _fileWatcher.EnableRaisingEvents = true;
        
        Console.Error.WriteLine($"[Watcher] Monitoring {solutionDir} ({extensions}) — debounce {debounceMs}ms");
    }

    private async void OnDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        List<string> paths;
        lock (_watchLock) { paths = _pendingPaths.ToList(); _pendingPaths.Clear(); }
        if (paths.Count == 0) return;
        
        try
        {
            await SyncDocumentsAsync(paths);
            Console.Error.WriteLine($"[Watcher] Synced {paths.Count} file(s)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Watcher] Sync failed: {ex.Message}");
        }
    }

    internal void StopFileWatcher()
    {
        _fileWatcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
```

**Integration:** Call `StartFileWatcher()` at end of `LoadSolutionAsync`. Call `StopFileWatcher()` before loading a new solution or on test teardown.

### A.2 — Configuration & Docs (0.5 days)

Environment variables to register in `server.json` and document in README:

| Variable | Default | Description |
|---|---|---|
| `SHARPLENS_WATCH_MODE` | `false` | Enable auto-sync on file changes |
| `SHARPLENS_WATCH_DEBOUNCE_MS` | `300` | Quiet period before triggering sync |
| `SHARPLENS_WATCH_EXTENSIONS` | `.cs,.razor,.cshtml` | File extensions to watch |

### A.3 — Tests (1 day)

```csharp
// WatcherTests.cs
public class WatcherTests : IAsyncLifetime
{
    private string _tempDir;
    private RoslynService _service;

    [Fact]
    public async Task Watcher_SyncsOnFileChange()
    {
        // Setup: create temp dir, create .sln, load solution with watch mode
        // Action: write to a .cs file
        // Wait: debounce period + 200ms
        // Assert: document content in solution matches file on disk
    }

    [Fact]
    public async Task Watcher_DebouncesMultipleChanges()
    {
        // Rapid 5 writes in 50ms → single sync call with all 5 paths
    }

    [Fact]
    public async Task Watcher_DisabledWhenModeNotSet()
    {
        // SHARPLENS_WATCH_MODE not set → no watcher, no crash
    }

    [Fact]
    public async Task Watcher_HandlesFileDelete()
    {
        // File deleted while watching → document removed from solution
    }

    [Fact]
    public async Task Watcher_HandlesFileRename()
    {
        // File renamed → old path removed, new path added
    }

    [Fact]
    public async Task Watcher_RecoversAfterError()
    {
        // Corrupt file that causes sync to fail → watcher still running for next change
    }
}
```

### A.4 — Ship (0.5 days)
- Version bump → 1.6.0
- Update CHANGELOG
- Verify all 543 existing tests pass
- PR review + merge
- **Ships independently — no Razor dependency.**

---

## Stream B: Razor Core Infrastructure (Week 2 Day 4 → Week 3 Day 5)

### Starts after Phase 0 spike passes and Watcher ships. Both devs.

**Owner: Both devs | 6 days**

### B.1 — RazorFileInfo Record (Dev 2, 0.5 days)

```csharp
// src/RazorFileInfo.cs
namespace SharpLensMcp;

internal sealed record RazorFileInfo
{
    public required string RazorFilePath { get; init; }          // "Pages/Counter.razor"
    public required DocumentId VirtualDocumentId { get; init; }   // Roslyn doc ID for generated C#
    public required string RazorSourceText { get; init; }         // raw .razor content
    public required string GeneratedSourceText { get; init; }     // generated C# output
    public required RazorCSharpDocument CSharpDocument { get; init; } // holds SourceMappings
    public required DateTime ProcessedAt { get; init; }
}
```

### B.2 — RazorService.Processing (Dev 1, 1.5 days)

```csharp
// src/RazorService.cs
public partial class RoslynService
{
    private readonly Dictionary<string, RazorFileInfo> _razorDocuments = new();
    private readonly ConcurrentDictionary<string, RazorProjectEngine> _engineCache = new();
    // Cache key: project file path

    internal RazorFileInfo ProcessRazorFile(string razorAbsPath, Project project)
    {
        // 1. Read source from disk
        var razorSource = File.ReadAllText(razorAbsPath);
        
        // 2. Get or create engine (cached per project directory)
        var engine = GetOrCreateRazorEngine(project);
        
        // 3. Process
        var sourceDoc = RazorSourceDocument.Create(razorSource, razorAbsPath);
        var codeDoc = engine.Process(sourceDoc);
        var csharpDoc = codeDoc.GetCSharpDocument();
        
        // 4. Create info (virtual doc added separately)
        var info = new RazorFileInfo
        {
            RazorFilePath = NormalizeRazorPath(razorAbsPath),
            VirtualDocumentId = default, // set after AddVirtualDoc
            RazorSourceText = razorSource,
            GeneratedSourceText = csharpDoc.GeneratedCode,
            CSharpDocument = csharpDoc,
            ProcessedAt = DateTime.UtcNow
        };
        
        // 5. Add virtual doc and update info
        info = info with { VirtualDocumentId = AddVirtualRazorDocument(project, info) };
        
        // 6. Cache
        var key = NormalizeRazorPath(razorAbsPath);
        _razorDocuments[key] = info;
        
        return info;
    }
    
    private RazorProjectEngine GetOrCreateRazorEngine(Project project)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath!)!;
        if (_engineCache.TryGetValue(projectDir, out var cached))
            return cached;
        
        var fileSystem = RazorProjectFileSystem.Create(projectDir);
        
        // Determine root namespace from project
        var rootNamespace = project.DefaultNamespace ?? project.Name;
        
        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            builder =>
            {
                builder.SetNamespace(rootNamespace);
                // Tag helpers added in Phase F
            });
        
        _engineCache[projectDir] = engine;
        return engine;
    }
    
    private DocumentId AddVirtualRazorDocument(Project project, RazorFileInfo info)
    {
        var fileName = Path.GetFileNameWithoutExtension(info.RazorFilePath);
        var relativeDir = Path.GetDirectoryName(info.RazorFilePath);
        
        // Synthetic path in obj/ — never collides with real files
        var virtualDir = Path.Combine(
            Path.GetDirectoryName(project.FilePath)!,
            "obj", "SharpLensMcp", "RazorGenerated",
            relativeDir ?? "");
        
        var virtualFileName = $"{fileName}_razor.g.cs";
        var virtualPath = Path.Combine(virtualDir, virtualFileName);
        
        var docId = DocumentId.CreateNewId(project.Id);
        var sourceText = SourceText.From(info.GeneratedSourceText);
        var folders = (relativeDir ?? "").Split(Path.DirectorySeparatorChar, 
            StringSplitOptions.RemoveEmptyEntries);
        
        _solution = _solution.AddDocument(
            docId, virtualFileName, sourceText, folders, virtualPath);
        
        return docId;
    }
    
    private string NormalizeRazorPath(string path)
    {
        // Convert absolute to solution-relative, forward slashes
        var normalized = path.Replace('\\', '/');
        var solutionDir = Path.GetDirectoryName(_solution!.FilePath)!.Replace('\\', '/');
        if (normalized.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[(solutionDir.Length + 1)..];
        return normalized;
    }
}
```

### B.3 — RazorService.Mapping (Dev 2, 1.5 days)

```csharp
// src/RazorService.Mapping.cs
public partial class RoslynService
{
    internal (Document Document, int Line, int Column)? 
        MapRazorPositionToCSharp(string razorFilePath, int razorLine, int razorColumn)
    {
        var info = GetRazorFileInfo(razorFilePath);
        if (info == null) return null;
        
        var razorOffset = GetOffset(info.RazorSourceText, razorLine, razorColumn);
        
        var mapping = info.CSharpDocument.SourceMappings
            .FirstOrDefault(m => 
                razorOffset >= m.OriginalSpan.AbsoluteIndex
                && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);
        
        if (mapping == null) return null; // position in pure markup
        
        // Linear interpolation within the mapping span
        var fraction = (double)(razorOffset - mapping.OriginalSpan.AbsoluteIndex) 
                     / mapping.OriginalSpan.Length;
        var generatedOffset = mapping.GeneratedSpan.AbsoluteIndex 
                            + (int)(fraction * mapping.GeneratedSpan.Length);
        
        var (line, col) = GetLineColumn(info.GeneratedSourceText, generatedOffset);
        var doc = _solution!.GetDocument(info.VirtualDocumentId);
        
        return doc != null ? (doc, line, col) : null;
    }
    
    internal (string FilePath, int Line, int Column)? 
        MapCSharpPositionToRazor(Document generatedDoc, int csharpLine, int csharpColumn)
    {
        // Find which razor file produced this generated doc
        var info = _razorDocuments.Values
            .FirstOrDefault(r => r.VirtualDocumentId == generatedDoc.Id);
        
        if (info == null) return null;
        
        var generatedOffset = GetOffset(info.GeneratedSourceText, csharpLine, csharpColumn);
        
        // Find mapping that covers this generated offset (reverse search)
        var mapping = info.CSharpDocument.SourceMappings
            .FirstOrDefault(m =>
                generatedOffset >= m.GeneratedSpan.AbsoluteIndex
                && generatedOffset < m.GeneratedSpan.AbsoluteIndex + m.GeneratedSpan.Length);
        
        if (mapping == null) return null;
        
        var fraction = (double)(generatedOffset - mapping.GeneratedSpan.AbsoluteIndex)
                     / mapping.GeneratedSpan.Length;
        var razorOffset = mapping.OriginalSpan.AbsoluteIndex
                        + (int)(fraction * mapping.OriginalSpan.Length);
        
        var (line, col) = GetLineColumn(info.RazorSourceText, razorOffset);
        return (info.RazorFilePath, line, col);
    }
    
    internal (string FilePath, int Line, int Column, int EndLine, int EndColumn)
        TranslateLocation(Location location)
    {
        if (location.SourceTree == null)
            return ("", 0, 0, 0, 0);
        
        var doc = _solution!.GetDocument(location.SourceTree);
        if (doc == null)
            return (location.SourceTree.FilePath,
                    location.GetLineSpan().StartLinePosition.Line,
                    location.GetLineSpan().StartLinePosition.Character,
                    location.GetLineSpan().EndLinePosition.Line,
                    location.GetLineSpan().EndLinePosition.Character);
        
        // Check if this is a razor-generated virtual document
        var razorMapping = MapCSharpPositionToRazor(
            doc, 
            location.GetLineSpan().StartLinePosition.Line,
            location.GetLineSpan().StartLinePosition.Character);
        
        if (razorMapping != null)
            return (FormatPath(razorMapping.Value.FilePath),
                    razorMapping.Value.Line,
                    razorMapping.Value.Column,
                    razorMapping.Value.Line,  // single-point locations
                    razorMapping.Value.Column);
        
        // Regular C# document — return as-is but formatted
        return (FormatPath(location.SourceTree.FilePath),
                location.GetLineSpan().StartLinePosition.Line,
                location.GetLineSpan().StartLinePosition.Character,
                location.GetLineSpan().EndLinePosition.Line,
                location.GetLineSpan().EndLinePosition.Character);
    }
    
    // Helpers
    internal bool IsRazorGeneratedDocument(Document document) =>
        _razorDocuments.Values.Any(r => r.VirtualDocumentId == document.Id);
    
    internal void InvalidateRazorFile(string razorFilePath)
    {
        var key = NormalizeRazorPath(razorFilePath);
        if (_razorDocuments.TryGetValue(key, out var info))
        {
            // Remove virtual doc from solution
            if (info != null)
                _solution = _solution.RemoveDocument(info.VirtualDocumentId);
            _razorDocuments.Remove(key);
        }
    }
    
    // Text offset ↔ line/column conversions
    private static int GetOffset(string text, int zeroBasedLine, int zeroBasedColumn)
    {
        var lines = text.Split('\n');
        if (zeroBasedLine >= lines.Length) return text.Length;
        return text.Split('\n').Take(zeroBasedLine).Sum(l => l.Length + 1) + zeroBasedColumn;
    }
    
    private static (int line, int column) GetLineColumn(string text, int offset)
    {
        var line = 0;
        var col = 0;
        for (int i = 0; i < Math.Min(offset, text.Length); i++)
        {
            if (text[i] == '\n') { line++; col = 0; }
            else col++;
        }
        return (line, col);
    }
}
```

### B.4 — Extend LoadSolutionAsync for Razor Detection (Dev 1, 1 day)

```csharp
// At the end of LoadSolutionAsync:
DiscoverRazorFiles();

private void DiscoverRazorFiles()
{
    foreach (var project in _solution!.Projects)
    {
        if (!IsRazorProject(project)) continue;
        
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir == null) continue;
        
        foreach (var razorPath in Directory.EnumerateFiles(
            projectDir, "*.razor", SearchOption.AllDirectories))
        {
            var key = NormalizeRazorPath(razorPath);
            if (!_razorDocuments.ContainsKey(key))
                _razorDocuments[key] = null!; // marker: discovered, not processed yet
        }
    }
}

private static bool IsRazorProject(Project project)
{
    return project.MetadataReferences.Any(r =>
        (r.Display ?? "").Contains("Microsoft.AspNetCore.Components"));
}

/// <summary>Get or lazily process a .razor file.</summary>
internal RazorFileInfo? GetRazorFileInfo(string razorFilePath)
{
    var key = NormalizeRazorPath(razorFilePath);
    
    if (!_razorDocuments.TryGetValue(key, out var info))
        return null;
    
    if (info == null)
    {
        // Find the owning project
        var project = FindProjectForFile(key);
        if (project == null) return null;
        
        // Lazy first-access processing
        info = ProcessRazorFile(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(_solution!.FilePath)!, key)),
            project);
    }
    
    return info;
}
```

### B.5 — Extend TryFindDocument for .razor Routing (Dev 2, 1 day)

```csharp
internal Document? TryFindDocument(string filePath)
{
    if (_documentCache.TryGetValue(filePath, out var cached))
        return cached;

    // NEW: .razor routing — redirect to virtual C# document
    if (filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
    {
        var normalized = NormalizeRazorPath(filePath);
        var razorInfo = GetRazorFileInfo(normalized);
        if (razorInfo != null)
        {
            var virtualDoc = _solution!.GetDocument(razorInfo.VirtualDocumentId);
            if (virtualDoc != null)
            {
                if (_enableCache)
                    _documentCache[filePath] = virtualDoc;
                return virtualDoc;
            }
        }
        return null;
    }

    // ... existing .cs document resolution ...
}
```

### B.6 — Core Infrastructure Tests (Both devs, 0.5 days)

```csharp
// RazorMappingTests.cs
public class RazorMappingTests
{
    [Fact]
    public void MapRazorToCSharp_Roundtrips_ForCodeBlockField()
    {
        // "_counter" at .razor line 3 → generated C# → back to .razor line 3
    }
    
    [Fact]
    public void MapRazorToCSharp_ReturnsNull_ForPureMarkupPosition()
    {
        // <h1>Hello</h1> has no C# equivalent
    }
    
    [Fact]
    public void TryFindDocument_RoutesRazorPath_ToVirtualCSharpDoc()
    {
        // "Pages/Counter.razor" → Document with FilePath like "obj/.../Counter_razor.g.cs"
    }
    
    [Fact]
    public void ProcessRazorFile_GeneratesCompilableCSharp()
    {
        // Generated C# must compile with zero errors
    }
    
    [Fact]
    public void IsRazorProject_DetectsBlazorProjects()
    {
        // Project referencing Microsoft.AspNetCore.Components → true
    }
    
    [Fact]
    public void GetRazorFileInfo_LazyProcessesOnFirstAccess()
    {
        // Before access: _razorDocuments[key] is null marker
        // After access: fully populated RazorFileInfo with virtual doc
    }
}
```

---

## Stream C1: Navigation Tools (Week 4, Days 1-2)

### Depends on: Phase B complete. Can run in parallel with C2.

**Owner: Dev 1 | 2 days**

### Shared Wrapper

```csharp
// In RoslynService.cs — shared by all position-based tools
private record RazorToolContext(
    Document Document,
    int Line,
    int Column,
    bool IsRazorFile,
    string? OriginalRazorPath);

private async Task<object?> PrepareRazorAwareContext(
    string filePath, int line, int column)
{
    try
    {
        var doc = await GetDocumentAsync(filePath);
        
        if (!filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return new RazorToolContext(doc, line, column, false, null);
        
        var mapped = MapRazorPositionToCSharp(filePath, line, column);
        if (mapped == null)
            return CreateErrorResponse(ErrorCodes.InvalidParameter,
                $"Position (line {line}, col {column}) is in Razor markup, not C# code. " +
                "Use a position inside an @code { } block or inline C# expression.");
        
        return new RazorToolContext(
            mapped.Value.Document, mapped.Value.Line, mapped.Value.Column, 
            true, filePath);
    }
    catch (FileNotFoundException)
    {
        return CreateErrorResponse(ErrorCodes.FileNotInSolution, 
            $"File not found in solution: {filePath}");
    }
}

private object TranslateSymbolResult(ISymbol symbol, Location location, 
    bool isRazor, string? razorPath)
{
    var (path, line, col, el, ec) = TranslateLocation(location);
    return new
    {
        name = symbol.Name,
        kind = symbol.Kind.ToString(),
        filePath = path,  // already formatted
        line, column = col
    };
}
```

### Adaptation Pattern for Each Tool (identical for all 9)

**Before (current code):**
```csharp
public async Task<object> GetSymbolInfoAsync(string filePath, int line, int column)
{
    EnsureSolutionLoaded();
    var document = await GetDocumentAsync(filePath);
    // ... run tool on document ...
}
```

**After:**
```csharp
public async Task<object> GetSymbolInfoAsync(string filePath, int line, int column)
{
    EnsureSolutionLoaded();
    
    var ctxResult = await PrepareRazorAwareContext(filePath, line, column);
    if (ctxResult is not RazorToolContext ctx)
        return ctxResult; // error response
    
    var document = ctx.Document;
    // ... run tool using (ctx.Line, ctx.Column) instead of (line, column) ...
    // ... translate all output locations with TranslateLocation() ...
    // ... if ctx.IsRazorFile, use ctx.OriginalRazorPath for file paths ...
}
```

**Tools adapted (all 2 lines changed each):**
| Tool | Input change | Output change |
|---|---|---|
| `get_symbol_info` | Use `ctx.Line`, `ctx.Column` | `TranslateLocation` on symbol location |
| `go_to_definition` | Same | `TranslateLocation` on definition location(s) |
| `find_references` | Same | `TranslateLocation` on each reference location |
| `find_implementations` | Same | `TranslateLocation` on each implementation location |
| `get_type_hierarchy` | Same | `TranslateLocation` on base/derived type locations |
| `get_method_overloads` | Same | `TranslateLocation` on overload locations |
| `get_containing_member` | Same | `TranslateLocation` on containing member location |
| `find_callers` | Same | `TranslateLocation` on caller locations |
| `get_outgoing_calls` | Same | `TranslateLocation` on callee locations |

---

## Stream C2: Analysis Tools (Week 4, Days 1-2)

### Depends on: Phase B complete. Can run in parallel with C1.

**Owner: Dev 2 | 2 days**

### Pattern (similar to C1, but tool-specific adaptations)

| Tool | Adaptation |
|---|---|
| `get_diagnostics` | If `.razor`: get compilation for virtual doc's project. Filter to virtual doc. Translate diagnostic locations. Also include `info.CSharpDocument.Diagnostics` (razor parse errors). |
| `analyze_data_flow` | Map `.razor (startLine,endLine)` → generated C# line range. Run analysis. Translate all variable declaration/reference locations. |
| `analyze_control_flow` | Same pattern. |
| `get_complexity_metrics` | Run on virtual C# tree. In output, report file path as `.razor` file, not generated path. |
| `get_call_graph` | Translate `FilePath` for each node in the graph. |
| `analyze_change_impact` | Translate input position. Translate all affected reference locations. |

---

## Stream D: Refactoring Tools (Week 4 Day 3 → Week 5 Day 2)

### Depends on: Phase B complete. Can partially overlap with Stream E.

**Owner: Dev 1 | 4 days**

### D.1 — Edit Translation Engine (1 day)

The core challenge: Roslyn produces edits on generated C#. We must reverse-map them to `.razor`.

```csharp
// In RazorService.cs
internal record RazorEdit(int StartOffset, int EndOffset, string NewText, string Description);

internal async Task<List<(string RazorPath, RazorEdit[] Edits)>> 
    TranslateRefactoringEdits(Solution newSolution)
{
    var result = new List<(string, RazorEdit[])>();
    var changes = newSolution.GetChanges(_solution);
    
    foreach (var projectChanges in changes.GetProjectChanges())
    {
        foreach (var docId in projectChanges.GetChangedDocuments())
        {
            if (!IsRazorGeneratedDocument(docId)) continue;
            
            var oldDoc = _solution!.GetDocument(docId);
            var newDoc = newSolution.GetDocument(docId);
            if (oldDoc == null || newDoc == null) continue;
            
            var razorInfo = _razorDocuments.Values
                .First(r => r.VirtualDocumentId == docId);
            
            var textChanges = await newDoc.GetTextChangesAsync(oldDoc);
            var edits = textChanges
                .Select(tc => TranslateTextChangeToRazor(tc, razorInfo))
                .Where(e => e != null)
                .Select(e => e!.Value)
                .ToArray();
            
            result.Add((razorInfo.RazorFilePath, edits));
        }
    }
    
    return result;
}

private RazorEdit? TranslateTextChangeToRazor(
    TextChange change, RazorFileInfo info)
{
    // For each change in generated C#:
    // 1. If change.Span is entirely outside any source mapping → null (generated-only code)
    // 2. If change.Span is within a mapping → translate to .razor offsets
    // 3. Handle: insertion, deletion, replacement
    
    var genStart = change.Span.Start;
    var genEnd = change.Span.End;
    
    var mappings = info.CSharpDocument.SourceMappings;
    
    // Find mappings covering the change range
    // Apply the inverse of the forward mapping logic
    
    // Returns null for changes in generated-only code (like BuildRenderTree)
}
```

### D.2 — Tool-by-Tool Adaptation (3 days)

**Pattern for each:**
```
1. PrepareRazorAwareContext → get virtual doc + translated position
2. Run Roslyn refactoring API on virtual doc → get new Solution
3. If preview: TranslateRefactoringEdits → return .razor diff preview
4. If apply: TranslateRefactoringEdits → apply to .razor on disk → regenerate virtual doc → clear caches
```

| # | Tool | Effort | Key Implementation |
|---|---|---|---|
| 1 | `rename_symbol` | 0.5 days | Run `Renamer.RenameSymbolAsync` on virtual doc. Single identifier rename → simple reverse-map. |
| 2 | `encapsulate_field` | 0.5 days | Roslyn code action "Encapsulate field" → generated C# gets property + updated references. Map back. |
| 3 | `inline_variable` | 0.5 days | Code action → variable removed, usage inlined. Simple delete + replace mapping. |
| 4 | `extract_variable` | 0.5 days | Insert `var x = expr;` before usage, replace expression with `x`. |
| 5 | `implement_missing_members` | 0.5 days | Insert stub methods at end of `@code` block. Text insertion only (no deletion). |
| 6 | `change_signature` | 0.5 days | `SolutionEditor` approach. Multi-file. Translate each changed document. |
| 7 | `extract_method` | 1 day | Complex: new method creation, call site replacement, parameter flow. Multiple insertions + replacements. |

**Safety pattern (applied to all 7):**
```csharp
if (preview)
{
    var edits = await TranslateRefactoringEdits(newSolution);
    return CreateSuccessResponse(new { edits, message = "Preview only. Set preview=false to apply." });
}

// Apply: snapshot → apply edits to .razor → regenerate virtual doc → verify compilation
SnapshotRazorFile(razorPath);
try
{
    ApplyEditsToDisk(edits);
    ProcessRazorFile(razorPath, project); // regenerate
    ClearCaches();
}
catch
{
    RestoreRazorFile(razorPath);
    throw;
}
```

---

## Stream E: Type, Discovery, Code Action Tools (Week 5, Days 1-5)

### Depends on: Phase B complete. Runs in parallel with Stream D.

**Owner: Dev 2 | 5 days**

### E1 — Type-Based Tools (2 days)

These tools search by type name, not file position. Virtual C# documents make symbols discoverable. The only adaptation: translate `Location` results.

```csharp
// Shared post-processing for all type-based results
private object TranslateTypeResult(TypeSymbolResult result)
{
    var (path, line, col, _, _) = TranslateLocation(result.Location);
    return new
    {
        result.Name,
        result.Kind,
        result.Namespace,
        filePath = path,
        line,
        column = col
    };
}
```

| Tool | Change |
|---|---|
| `search_symbols` | Post-process: `TranslateLocation` on each result's location |
| `semantic_query` | Same |
| `get_type_members` | Same |
| `get_type_members_batch` | Same |
| `get_method_signature` | Same |
| `get_derived_types` | Same |
| `get_base_types` | Same |
| `get_attributes` | Same |
| `check_type_compatibility` | No change (no paths in output) |
| `get_instantiation_options` | Include constructors from `.razor` types |

### E2 — Discovery Tools (1 day)

| Tool | Change |
|---|---|
| `get_project_structure` | Add `razorFileCount`, `isRazorProject` fields. Include `.razor` entries in documents list. |
| `dependency_graph` | No change (project-level) |
| `get_di_registrations` | Scan `@inject` directives in generated C# |
| `find_reflection_usage` | Include virtual documents in scan scope |
| `find_circular_dependencies` | No change |
| `get_nuget_dependencies` | No change |
| `get_source_generators` | No change |
| `get_generated_code` | Works for razor-generated code (virtual docs) |

### E3 — Compound Tools (1 day)

| Tool | Change |
|---|---|
| `get_type_overview` | Include `.razor` file path in output |
| `analyze_method` | Works if navigation+analysis constituents work |
| `get_file_overview` | Accept `.razor` path; run on virtual doc |
| `get_method_source` | Return generated C# with note it's derived from `.razor` |
| `get_method_source_batch` | Same |
| `get_project_health` | Include `.razor` diagnostics |

### E4 — Code Actions (1 day)

```csharp
// get_code_actions_at_position:
// If .razor → PrepareRazorAwareContext → get code actions on virtual doc
// → return action titles (user sees standard Roslyn actions)

// apply_code_action_by_title:
// If .razor → PrepareRazorAwareContext → apply on virtual doc
// → TranslateRefactoringEdits → preview or apply to .razor
```

---

## Phase F: sync_documents + Tag Helpers (Week 6, Days 1-2)

### Depends on: Streams D and E complete.

**Owner: Both devs (1 task each) | 2 days**

### F1 — sync_documents for Razor (Dev 1, 1 day)

```csharp
// In SyncDocumentsAsync, for each path:
if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
{
    var normalizedPath = NormalizeRazorPath(path);
    var existsOnDisk = File.Exists(path);
    var inRegistry = _razorDocuments.ContainsKey(normalizedPath);
    
    if (existsOnDisk && inRegistry)
    {
        // Changed: re-process and update virtual doc
        var project = FindProjectForFile(normalizedPath);
        var info = ProcessRazorFile(path, project!);
        _razorDocuments[normalizedPath] = info;
        updatedDocs.Add(normalizedPath);
    }
    else if (existsOnDisk && !inRegistry)
    {
        // New file: process and add
        var project = FindProjectForFile(normalizedPath);
        var info = ProcessRazorFile(path, project!);
        addedDocs.Add(normalizedPath);
    }
    else if (!existsOnDisk && inRegistry)
    {
        // Deleted: remove from registry and solution
        InvalidateRazorFile(normalizedPath);
        removedDocs.Add(normalizedPath);
    }
    // Neither: no-op
}
```

### F2 — Tag Helper Support (Dev 2, 1 day)

```csharp
// Attempt to use Microsoft.CodeAnalysis.Razor's tag helper discovery
// If public API exists:

private ImmutableArray<TagHelperDescriptor> DiscoverTagHelpers(Project project)
{
    // Check cache first
    var cacheKey = project.FilePath!;
    if (_tagHelperCache.TryGetValue(cacheKey, out var cached))
        return cached;
    
    // Try the library API (exact method TBD from Phase 0 audit)
    // Fallback: scan known Blazor assemblies for [HtmlTargetElement]
    
    var tagHelpers = /* library API or fallback */;
    _tagHelperCache[cacheKey] = tagHelpers;
    return tagHelpers;
}

// Integration into GetOrCreateRazorEngine:
builder.AddTagHelpers(DiscoverTagHelpers(project));
```

If the library API is unavailable, basic tag helper support scans:
```
Microsoft.AspNetCore.Components.dll        → Input*, EditForm, etc.
Microsoft.AspNetCore.Components.Web.dll    → RouteView, etc.
Microsoft.AspNetCore.Components.Forms.dll  → InputText, InputNumber, etc.
```

This is sufficient for the most common Blazor components to resolve correctly.

---

## Phase G: Testing + Polish + Ship (Week 6 Day 3 → Week 7 Day 5)

### Depends on: All streams complete.

**Owner: Both devs | 6 days**

### G1 — Integration Tests (Both, 2 days)

Create a real Blazor test fixture:

```
tests/SharpLensMcp.Tests.RazorFixture/
  SharpLensMcp.Tests.RazorFixture.csproj  (Blazor WebAssembly or Server SDK)
  Pages/
    Counter.razor              — @code { int count; void Increment() => count++; }
    DataBinding.razor          — @bind-Value, @bind:event, @bind:format
    EventHandling.razor        — @onclick, @onchange, @oninput
    TypeParam.razor            — @typeparam TItem
    NestedComponents.razor     — <Child Param="x" />
  Shared/
    Layout.razor               — @Body
  _Imports.razor               — @using, @inject
```

Integration test class:

```csharp
// RazorIntegrationTests.cs — uses real Blazor project loaded via MSBuildWorkspace
public class RazorIntegrationTests : RoslynServiceTestBase
{
    // Navigation
    [Fact] public async Task GetSymbolInfo_OnRazorField_ReturnsSymbol() { }
    [Fact] public async Task GoToDefinition_OnRazorMethodCall_JumpsToDeclaration() { }
    [Fact] public async Task FindReferences_OnRazorProperty_FindsAllUsages() { }
    [Fact] public async Task FindImplementations_OnInterface_InRazorCodeBlock() { }
    [Fact] public async Task GetMethodOverloads_OnRazorMethod_ReturnsOverloads() { }
    [Fact] public async Task FindCallers_OnRazorMethod_FindsCallers() { }
    
    // Analysis
    [Fact] public async Task GetDiagnostics_RazorFile_NoErrors() { }
    [Fact] public async Task GetDiagnostics_IncludesRazorParseErrors() { }
    [Fact] public async Task AnalyzeDataFlow_InRazorBlock_TracksVariable() { }
    [Fact] public async Task GetComplexityMetrics_ReportsRazorPath() { }
    
    // Type discovery
    [Fact] public async Task SearchSymbols_FindsComponentTypes_AfterProcessing() { }
    [Fact] public async Task GetDerivedTypes_OnComponentBase_IncludesRazorComponents() { }
    [Fact] public async Task GetTypeMembers_ShowsRazorParameters() { }
    
    // sync_documents
    [Fact] public async Task SyncDocuments_AfterEditingRazor_UpdatesSymbols() { }
    [Fact] public async Task SyncDocuments_AfterAddingRazorFile_AddsVirtualDoc() { }
}
```

### G2 — Edge Case Tests (Both, 2 days)

| Edge Case | Tools Affected | Verification |
|---|---|---|
| `.razor` with no `@code` block (pure markup) | All position-based | Return appropriate error or empty results |
| `.razor` + `.razor.cs` code-behind (partial class) | Find references, rename | References must span both files |
| `.razor` with `@inherits NonComponentBase` | GetTypeHierarchy | Shows correct base type |
| `.razor` in a class library (RCL) | Project detection, all tools | Works same as Blazor app |
| Multiple `.razor` files referencing each other | Find references | Cross-file refs work |
| `.razor` file externally deleted | sync_documents | Virtual doc removed, no crash |
| Very large `.razor` (500+ lines) | All | Performance acceptable, no timeout |
| Deeply nested C# in markup: `@if { @foreach { @if { ... } } }` | Position mapping | Accuracy maintained through nesting |
| `@code` block with `#region` / `#pragma` | All tools | Roslyn handles these normally in generated C# |
| `.razor` with `@attribute [Authorize]` | GetAttributes, FindAttributeUsages | Attribute discovered on generated class |

### G3 — Refactoring-Specific Tests (Dev 1, 1 day)

```csharp
[Fact] public async Task RenameSymbol_OnRazorField_RenamesAllOccurrencesInRazor() { }
[Fact] public async Task RenameSymbol_Preview_ShowsRazorDiffNotCSharpDiff() { }
[Fact] public async Task RenameSymbol_Apply_PreservesMarkupIntact() { }
[Fact] public async Task ExtractMethod_Preview_ShowsCorrectInsertionPoints() { }
[Fact] public async Task ExtractMethod_Apply_ProducesValidRazorAfterEdit() { }
[Fact] public async Task ChangeSignature_OnRazorMethod_UpdatesAllCallers() { }
[Fact] public async Task ImplementMissingMembers_InsertsStubsInCodeBlock() { }
[Fact] public async Task Refactoring_AfterApply_RegeneratedCSharpCompiles() { }
```

### G4 — Performance Profiling (Dev 2, 0.5 days)

- Profile `LoadSolutionAsync` with 50+ `.razor` files
- Verify lazy processing prevents slowdown on solution load
- Profile `sync_documents` for single `.razor` change: target < 150ms
- Profile first query to a `.razor` file (triggers lazy processing): target < 500ms
- Profile `find_references` on a `.razor` symbol: should be within 2x of `.cs` equivalent

### G5 — Documentation (Dev 2, 0.5 days)

- Update README: new "Blazor / Razor Support" section
- Update tool descriptions: 29 tools that accept `filePath` mention `.razor` support
- Update AI Agent Configuration Tips: add Blazor example
- Update CHANGELOG: v2.0.0 release notes
- Update `server.json`: version bump, new env vars (`SHARPLENS_WATCH_*`)

### G6 — Final Regression Run (Both, 0.5 days)

```bash
dotnet test -c Release --verbosity normal
```

- All 543 existing tests must pass
- All new Razor tests must pass
- All watcher tests must pass

### G7 — Ship (Both, 0.5 days)

- Bump version to 2.0.0
- Update CHANGELOG.md
- PR review
- Merge
- Tag release

---

## Resource Plan Summary

| Week | Dev 1 | Dev 2 | Deliverable |
|---|---|---|---|
| **1** | Phase 0: Razor pipeline spike | Phase 0: API surface audit | Spike passes → green light |
| **2** | Stream A: Watcher (days 1-3), then joins B | Phase 0 cont. (day 1), then Stream B: Core infra | Watcher ships (v1.6.0). Core infra begins. |
| **3** | Stream B: Core infra (continued) | Stream B: Core infra (continued) | Core infra complete. TryFindDocument routes .razor. |
| **4** | Stream C1: Navigation tools (2 days) then Stream D: Refactoring (day 3) | Stream C2: Analysis tools (2 days) then Stream E: Type tools (day 3) | All position-based tools work on .razor. |
| **5** | Stream D: Refactoring (4 days total finish) | Stream E: Type + Discovery + Code Actions (5 days) | Refactoring preview works. All remaining tools adapted. |
| **6** | Phase F1: sync_documents (day 1) then Phase G: Testing (days 2-5) | Phase F2: Tag helpers (day 1) then Phase G: Testing (days 2-5) | sync_documents handles .razor. Integration tests pass. |
| **7** | Phase G: Testing + Polish + Ship | Phase G: Testing + Polish + Ship | v2.0.0 ships. |

**Total: 7 weeks (2 devs) or 10 weeks (1 dev).**

---

## Risk Gates

| Gate | When | Criteria | If FAIL |
|---|---|---|---|
| **G0: Spike** | End of Week 1 | Source mappings accurate. Package compatible. Performance acceptable. | Switch to custom mapping approach (+2 weeks) |
| **G1: Watcher** | Mid Week 2 | All watcher tests pass. Existing 543 tests pass. | Ship without watcher (no blocker for Razor) |
| **G2: Core Infra** | End of Week 3 | TryFindDocument routes .razor. Round-trip mapping tests pass. Virtual docs compile. | Debug mapping logic; may need library version change |
| **G3: Navigation + Analysis** | End of Week 4 | All position-based tools return correct .razor paths and line/col. | Per-tool debugging; translation layer handles edge cases |
| **G4: Refactoring Preview** | End of Week 5 | Preview mode shows correct .razor diffs. No file corruption. | Disable apply mode; ship preview-only for v2.0 |
| **G5: Full Suite** | End of Week 7 | All tests pass. Integration tests against Blazor project pass. | Fix regressions; may delay ship by 1 week |

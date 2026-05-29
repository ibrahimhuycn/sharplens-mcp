# Razor (.razor) + Filesystem Watcher — Gap Study & Implementation Plan

> **Revision 2** — Switched from building position mapping from scratch to hosting `Microsoft.CodeAnalysis.Razor` services internally. This is the same library VS Code C# DevKit, Visual Studio, and Rider use for Razor support.

## Table of Contents
1. [Current Architecture Assumptions](#1-current-architecture-assumptions)
2. [Razor Architecture Reality](#2-razor-architecture-reality)
3. [Why Not VS Code MCP?](#3-why-not-vs-code-mcp)
4. [The Correct Approach: Host Microsoft.CodeAnalysis.Razor](#4-the-correct-approach-host-microsoftcodeanalysisrazor)
5. [Gap Analysis](#5-gap-analysis)
6. [Filesystem Watcher](#6-filesystem-watcher)
7. [Implementation Phases](#7-implementation-phases)
8. [Risks & Trade-offs](#8-risks--trade-offs)

---

## 1. Current Architecture Assumptions

SharpLensMcp treats every document as a C# file Roslyn can parse natively:

- `MSBuildWorkspace.OpenSolutionAsync()` loads projects; documents come from `<Compile Include>` items
- `TryFindDocument(filePath)` scans `project.Documents` — all `Document` objects with `CSharpSyntaxTree`
- Every tool calls `document.GetSemanticModelAsync()` — assumes the tree is `CSharpSyntaxTree`
- `sync_documents` reads disk files and calls `_solution.WithDocumentText()`

All of these are **violated** for `.razor` files. But the solution is not to rewrite them — it's to add a shim layer that makes `.razor` files look like C# documents to the existing tool pipeline.

---

## 2. Razor Architecture Reality

### 2.1 What MSBuildWorkspace Sees

Blazor projects loaded via `MSBuildWorkspace`:
- `.cs` files → Roslyn `Document` with `CSharpSyntaxTree` ✓
- `.razor` files → **NOT** in `project.Documents`. At best they're `AdditionalDocuments`.

### 2.2 How The Razor Tools Work (VS Code, VS, Rider)

All major IDEs use the same approach, backed by `Microsoft.CodeAnalysis.Razor`:

```
.razor file on disk
      │
      ▼
RazorProjectEngine.Process()          ← Microsoft.AspNetCore.Razor.Language
      │
      ├── RazorCSharpDocument (generated C# + SourceMappings)
      │
      ▼
Virtual C# document in Roslyn workspace
      │
      ▼
Roslyn APIs (FindReferences, Rename, etc.) operate on virtual C#
      │
      ▼
SourceMappings translate results back to .razor line/column
```

The key libraries:

| Library | Role |
|---|---|
| `Microsoft.AspNetCore.Razor.Language` | Low-level Razor parser. Compiles `.razor` → C#. Provides `RazorProjectEngine`, `RazorCodeDocument`, `RazorCSharpDocument`, source mappings. |
| `Microsoft.CodeAnalysis.Razor` | Higher-level services. Provides document lifecycle management, position mapping utilities, tag helper resolution, workspace integration. Used by the Razor LSP server. |

SharpLensMcp should use both: the low-level library for compilation, the higher-level one for the services it exposes. The Phase 0 spike determines exactly which services are usable.

---

## 3. Why Not VS Code MCP?

The `@vscode-mcp/vscode-mcp-server` is a generic bridge — it sends VS Code commands (open file, set cursor, run command) via Unix socket. It's an editor remote-control, not a semantic analysis server.

| Capability | VS Code MCP | SharpLensMcp |
|---|---|---|
| Go to definition | Via LSP (basic) | ✓ |
| Find references | Via LSP (basic) | ✓ with kind classification |
| Find async methods missing CancellationToken | ❌ | ✓ |
| Impact analysis (what breaks?) | ❌ | ✓ |
| Dead code detection | ❌ | ✓ |
| Complexity metrics | ❌ | ✓ |
| Safe refactoring with preview | ❌ | ✓ |
| Batch operations | ❌ | ✓ |
| God object detection | ❌ | ✓ |
| Project health dashboard | ❌ | ✓ |
| DI registration scanning | ❌ | ✓ |
| Call graph traversal | ❌ | ✓ |
| External type info | ❌ | ✓ |
| 54 more tools | ❌ | ✓ |
| **Razor position mapping** | Via built-in LSP (black box) | Via `Microsoft.CodeAnalysis.Razor` (hosted) |

Routing through VS Code would lose 80% of SharpLensMcp's value. The only thing VS Code does better for Razor is having the Razor Language Server already running — and we can get that same capability by hosting `Microsoft.CodeAnalysis.Razor` directly.

---

## 4. The Correct Approach: Host Microsoft.CodeAnalysis.Razor

### 4.1 Architecture

```
MCP client (AI agent)
    │
    ▼
McpServer.cs              ← "Here's a .razor file at line 5, col 12"
    │
    ▼
RoslynService.cs          ← detects .razor extension → delegates to RazorService
    │
    ▼
RazorService.cs           ← thin wrapper around Microsoft.CodeAnalysis.Razor
    │
    ├── Process .razor → generated C#           (via RazorProjectEngine)
    ├── Add virtual C# doc to Roslyn workspace
    ├── Map .razor position → virtual C# position (via SourceMappings / RazorDocumentMappingService)
    │
    ▼
Existing 67 tools          ← operate on the virtual C# document transparently
    │
    ▼
RazorService maps results back to .razor paths + line/col
```

### 4.2 What We Get For Free

By hosting `Microsoft.CodeAnalysis.Razor`, these hard problems become library calls:

| Problem | Without library (v1 gap study) | With `Microsoft.CodeAnalysis.Razor` |
|---|---|---|
| Compile .razor → C# | Manual `RazorProjectEngine` setup, config, imports | `RazorProjectEngine.Create()` — same |
| Position mapping (.razor → C#) | Custom algorithm with edge cases, interpolation, fallbacks | `RazorCSharpDocument.SourceMappings` — proven mappings used by all IDEs |
| Position mapping (C# → .razor) | Custom reverse algorithm | Same mappings, inverted |
| Tag helper discovery | Assembly scanning, attribute reflection, caching | May be available via `TagHelperDescriptor` APIs (Phase 0 verifies) |
| Document lifecycle | Manual add/update/remove in sync_documents | May be available via `RazorProjectService` (Phase 0 verifies) |

### 4.3 Phase 0 Verification Checklist

Before committing to this approach, the spike MUST verify:

1. `Microsoft.CodeAnalysis.Razor` NuGet package version compatible with net8.0
2. `RazorProjectEngine.Process()` produces valid C# for sample Blazor components
3. Source mappings are character-accurate (verified by round-trip test)
4. What public APIs exist in the package (reflection inspection):
   - Is `RazorDocumentMappingService` public? If not, can we use `RazorCSharpDocument.SourceMappings` directly?
   - Is `RazorProjectService` hostable? If not, we implement a thin document tracker ourselves.
   - Tag helper discovery: public API or internal?
5. Generated C# compiles against the real project's compilation references
6. Performance: processing 50 `.razor` files takes < 2 seconds

---

## 5. Gap Analysis

### Gap A — Project Detection

**Current state:** `LoadSolutionAsync` treats all projects uniformly.

**Required:** Identify which projects contain `.razor` files.

**Approach:**
```csharp
private static bool IsRazorProject(Project project)
{
    // Check metadata references for Blazor assemblies
    return project.MetadataReferences.Any(r =>
        (r.Display ?? "").Contains("Microsoft.AspNetCore.Components"));
}

// After MSBuildWorkspace loads: scan project directories for *.razor
var projectDir = Path.GetDirectoryName(project.FilePath);
var razorFiles = Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories);
```

**Effort: 0.5 days**

### Gap B — .razor File Processing
**Effort: 1 day** (was 1 week in v1 plan)

```csharp
// src/RazorService.cs
internal record RazorFileInfo(
    string RazorFilePath,
    DocumentId VirtualDocumentId,
    string GeneratedSourceText,
    RazorCSharpDocument CSharpDocument  // holds SourceMappings
);

internal RazorFileInfo ProcessRazorFile(string razorFilePath, Project project)
{
    // 1. Read .razor source from disk
    // 2. Get or create RazorProjectEngine (cached per project)
    // 3. Process → RazorCodeDocument → RazorCSharpDocument
    // 4. Extract GeneratedCode
    // 5. Add virtual C# document to Roslyn Solution
    // 6. Return RazorFileInfo
}
```

### Gap C — Position Mapping
**Effort: 2 days** (was 2 weeks in v1 plan)

Instead of building a custom mapping engine, we use the source mappings from `RazorCSharpDocument`:

```csharp
internal (Document Doc, int Line, int Col)? MapRazorToCSharp(
    string razorPath, int razorLine, int razorCol)
{
    var info = GetRazorFileInfo(razorPath);
    var razorOffset = GetOffset(info.RazorSource, razorLine, razorCol);
    
    // Use the Razor library's source mappings
    var mapping = info.CSharpDocument.SourceMappings
        .FirstOrDefault(m => razorOffset >= m.OriginalSpan.AbsoluteIndex 
                          && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);
    
    if (mapping == null) return null; // position in pure HTML
    
    // Linear map within the span
    var fraction = (double)(razorOffset - mapping.OriginalSpan.AbsoluteIndex) 
                 / mapping.OriginalSpan.Length;
    var generatedOffset = mapping.GeneratedSpan.AbsoluteIndex 
                        + (int)(fraction * mapping.GeneratedSpan.Length);
    
    var (line, col) = GetLineColumn(info.GeneratedSourceText, generatedOffset);
    var doc = _solution!.GetDocument(info.VirtualDocumentId);
    return (doc!, line, col);
}
```

### Gap D — Tool-by-Tool Impact (Revised)

With position mapping handled by the Razor library, the tool adaptation effort drops dramatically:

| Tool Category | Count | Adaptation Pattern | Effort |
|---|---|---|---|
| **Position-based navigation** | 9 | `PrepareRazorAwareContext(filePath, line, col)` → translate input, run query, translate output locations | 2 days |
| **File-based tools** | 6 | Accept `.razor` path → redirect to virtual doc → post-filter paths | 1 day |
| **Refactoring tools** | 7 | Run on virtual C# → extract text changes → reverse-map to `.razor` positions → apply to `.razor` disk file | 4 days |
| **Type-based tools** | 10 | Include symbols from virtual C# docs. Translate result file paths. | 2 days |
| **Compound tools** | 5 | Inherit from constituent tools | 1 day |
| **Discovery tools** | 6 | Add `.razor` file count to project structure. Scan `@inject` directives. | 1 day |
| **Audit/Quality** | 4 | Include virtual C# in scans, exclude generated-only code from results | 1 day |
| **Code actions** | 4 | Run on virtual C# → translate edits to `.razor` | 2 days |
| **Infrastructure** | 7 | `sync_documents` handles `.razor` regeneration. `load_solution` detects Razor projects. | 2 days |

### Gap E — `sync_documents` for Razor
**Effort: 1 day**

For `.razor` file paths in `sync_documents`:
- File changed → re-run `ProcessRazorFile` → update virtual doc with `_solution.WithDocumentText()`
- File added → process + add virtual doc + register in `_razorDocuments`
- File deleted → remove from `_razorDocuments` + remove virtual doc from solution
- Clear caches

### Gap F — Tag Helper Support
**Effort: 1 day** (was 1 week)

Attempt to use `Microsoft.CodeAnalysis.Razor`'s tag helper discovery. If the public API doesn't expose it, fall back to a minimal implementation:

```csharp
// Minimal tag helper discovery for known Blazor assemblies
private static readonly string[] BlazorComponentAssemblies = new[]
{
    "Microsoft.AspNetCore.Components",
    "Microsoft.AspNetCore.Components.Web",
    "Microsoft.AspNetCore.Components.Forms"
};

// If the project references any of these, we register their tag helpers
// by scanning for [HtmlTargetElement] in the assembly
```

For Phase 0: try the full API. If unavailable, basic tag helper support is still useful and can be enhanced later.

---

## 6. Filesystem Watcher

Unchanged from original plan. Summary:

- In-process `FileSystemWatcher` in `RoslynService.Watcher.cs`
- Enabled via `SHARPLENS_WATCH_MODE=true`
- Debounced (300ms) auto-sync on `.cs`, `.razor`, `.cshtml` changes
- Calls `SyncDocumentsAsync(paths)` internally
- **Effort: 3-4 days, independent of Razor work**

---

## 7. Implementation Phases (Revised)

### Timeline: 5-7 weeks (down from 10-12)

```
          Week 1        Week 2        Week 3        Week 4        Week 5        Week 6        Week 7

Dev 1:  ─[Phase 0: Spike]──[Watcher (A)]──[Razor Core Infra (B)]──[Navigation (C1) + Analysis (C2)]────[Refactoring (D)]────────────[Testing + Polish (G)]──
Dev 2:  ─[Phase 0: Spike]──[Phase 0 cont.]──[Razor Core Infra (B)]──[Type Tools (E1)]──[Discovery (E2) + Code Actions]──[sync_docs + Tag Helpers (F)]──[Testing (G)]──

          ▼              ▼              ▼              ▼              ▼              ▼              ▼
Milestones:           [Spike        [Watcher       [Core infra   [Nav+Analysis  [Refactoring   [ALL TESTS
                     PASS/FAIL       SHIPS!]        ready!]       done!]         done!]         PASS!]
                     decision]
```

### Phase 0 — Feasibility Spike (Week 1, Days 1-3)
**Both developers. MUST complete before any parallel work.**

**Goal:** Verify `Microsoft.CodeAnalysis.Razor` works within SharpLensMcp's constraints.

**Tasks:**

1. Add package reference to `SharpLensMcp.csproj`:
   ```xml
   <PackageReference Include="Microsoft.CodeAnalysis.Razor" Version="6.0.*" />
   ```
   (Version depends on net8.0 compatibility. Test during spike.)

2. Create a spike console app (or test class) that:
   - Loads a `.razor` file from a sample Blazor project
   - Creates `RazorProjectEngine` with appropriate configuration
   - Processes the `.razor` → gets `RazorCodeDocument` → `RazorCSharpDocument`
   - Inspects `SourceMappings` — verifies character-level accuracy with a round-trip test
   - Adds generated C# as a virtual document to an `AdhocWorkspace`
   - Runs `FindReferencesAsync` on a symbol from the generated C# and verifies the results
   - Maps result locations back to `.razor` positions using source mappings

3. **Reflection audit** of `Microsoft.CodeAnalysis.Razor`:
   - `typeof(Microsoft.CodeAnalysis.Razor.*).Assembly.GetExportedTypes()`
   - List all public types and their public methods
   - Identify: document mapping service, project service, tag helper APIs
   - Determine which can be used directly vs. which need workarounds

4. **Performance test**: Process 50 `.razor` files from a real Blazor project, measure wall clock time.

**Acceptance criteria:**
- Generated C# compiles against project references (no phantom errors for `ComponentBase`, `[Parameter]`, etc.)
- Source mapping round-trip: map `.razor` → C# → `.razor` returns original position (±2 chars acceptable)
- Processing 50 files takes < 3 seconds
- Clear answer on which `Microsoft.CodeAnalysis.Razor` services are usable

**Go/No-Go:** Source mappings must be reliable. If they fail on basic constructs (`@code`, `@bind`, `@onclick`), we reconsider the approach.

### Phase A — Filesystem Watcher (Week 1 Day 4 → Week 2 Day 3)
**Owner: Dev 1 | 3-4 days. Independent of Razor work.**

Same as original plan. Can run in parallel with Phase 0 if Dev 2 handles the spike.

**Deliverables:**
- `src/RoslynService.Watcher.cs`
- Env vars: `SHARPLENS_WATCH_MODE`, `SHARPLENS_WATCH_DEBOUNCE_MS`, `SHARPLENS_WATCH_EXTENSIONS`
- Watcher tests
- Can ship independently (v1.6.0)

### Phase B — Razor Core Infrastructure (Week 2 Day 4 → Week 3 Day 5)
**Owner: Both devs | 6 days**

**Deliverables:**

1. **`RazorFileInfo` record** (Dev 2, 0.5 days)
   ```csharp
   internal sealed record RazorFileInfo
   {
       public required string RazorFilePath { get; init; }
       public required DocumentId VirtualDocumentId { get; init; }
       public required string RazorSourceText { get; init; }
       public required string GeneratedSourceText { get; init; }
       public required RazorCSharpDocument CSharpDocument { get; init; }
       public required DateTime ProcessedAt { get; init; }
   }
   ```

2. **`RazorService.Processing`** (Dev 1, 1.5 days)
   - `ProcessRazorFile(string path, Project project)` → `RazorFileInfo`
   - `GetOrCreateRazorEngine(Project project)` with caching
   - `AddVirtualRazorDocument(RazorFileInfo, Project)` → `DocumentId`
   - Lazy processing: `.razor` files discovered on load, C# generated on first access

3. **`RazorService.Mapping`** (Dev 2, 1.5 days)
   - `MapRazorToCSharp(string path, int line, int col)` → `(Document, int, int)?`
   - `MapCSharpToRazor(Document, int line, int col)` → `(string path, int, int)?`
   - `TranslateLocation(Location)` → `(string path, int line, int col, int el, int ec)` — handles both virtual and real documents

4. **Extend `LoadSolutionAsync`** (Dev 1, 1 day)
   - `IsRazorProject(Project)` detection
   - `DiscoverRazorFiles()` — scan project directories, register in `_razorDocuments`
   - `GetRazorFileInfo(string path)` — lazy process on first access

5. **Extend `TryFindDocument`** (Dev 2, 1 day)
   - `.razor` path → look up `RazorFileInfo` → return virtual C# `Document`
   - Cache: razor file path key → virtual document value

6. **Tests** (Both devs, 0.5 days)
   - Round-trip mapping accuracy
   - Virtual document resolution
   - Lazy processing behavior
   - Cache invalidation

### Stream C1 — Navigation Tools (Week 4, Days 1-3)
**Owner: Dev 1 | 2 days (down from 4)**

Nine tools share the same pattern. Create a shared helper:

```csharp
private async Task<RazorToolContext> PrepareRazorAwareContext(
    string filePath, int line, int column)
{
    var doc = await GetDocumentAsync(filePath);
    // TryFindDocument now routes .razor → virtual doc
    
    if (!filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        return new(doc, line, column, IsRazor: false);
    
    var mapped = MapRazorToCSharp(filePath, line, column);
    if (mapped == null)
        return CreateError(ErrorCodes.InvalidParameter, 
            "Position is in markup, not C# code");
    
    return new(mapped.Value.Doc, mapped.Value.Line, mapped.Value.Col, 
               IsRazor: true, RazorPath: filePath);
}
```

Each navigation tool: wrap input with `PrepareRazorAwareContext` → run existing logic on the context document → `TranslateLocation` on all result locations.

| Tool | Lines Changed |
|---|---|
| `get_symbol_info` | ~5 lines |
| `go_to_definition` | ~5 lines |
| `find_references` | ~8 lines (translate all ref locations) |
| `find_implementations` | ~5 lines |
| `get_type_hierarchy` | ~5 lines |
| `get_method_overloads` | ~5 lines |
| `get_containing_member` | ~5 lines |
| `find_callers` | ~5 lines |
| `get_outgoing_calls` | ~5 lines |

### Stream C2 — Analysis Tools (Week 4, Days 1-3)
**Owner: Dev 2 | 2 days (down from 4)**

Same pattern, slightly different adaptations:

| Tool | Adaptation |
|---|---|
| `get_diagnostics` | Run on virtual doc's project compilation. Translate diagnostic locations to `.razor`. Include `RazorCSharpDocument.Diagnostics` (razor parse errors). |
| `analyze_data_flow` | Map `.razor` line range → generated C# range → run analysis → translate variable locations. |
| `analyze_control_flow` | Same pattern. |
| `get_complexity_metrics` | Run on virtual C#; report path as `.razor`. |
| `get_call_graph` | Translate node file paths in result. |
| `analyze_change_impact` | Translate input position + output locations. |

### Stream D — Refactoring Tools (Week 4 Day 4 → Week 5 Day 5)
**Owner: Dev 1 | 4 days (down from 8)**

**The key insight:** Roslyn refactorings produce `TextChange` objects on the generated C#. We reverse-map these to `.razor` character offsets using source mappings, then apply the text edits to the `.razor` file on disk.

```csharp
internal List<RazorEdit> TranslateRefactoringToRazor(
    Solution newSolution, RazorFileInfo razorInfo)
{
    var oldDoc = _solution!.GetDocument(razorInfo.VirtualDocumentId);
    var newDoc = newSolution.GetDocument(razorInfo.VirtualDocumentId);
    var textChanges = await newDoc.GetTextChangesAsync(oldDoc);
    
    return textChanges.Select(tc =>
    {
        var razorSpan = TranslateGeneratedSpanToRazor(
            tc.Span.Start, tc.Span.Length, razorInfo);
        return new RazorEdit(razorSpan.Start, razorSpan.End, tc.NewText);
    }).ToList();
}
```

**Tool adaptation order (easiest first):**

| # | Tool | Key Challenge | Effort |
|---|---|---|---|
| 1 | `rename_symbol` | Single identifier rename. Find in generated C#, apply, reverse-map to .razor. | 0.5 days |
| 2 | `encapsulate_field` | Property generation in @code block. Text insertion. | 0.5 days |
| 3 | `inline_variable` | Remove + inline. Text deletion + insertion. | 0.5 days |
| 4 | `extract_variable` | Insert declaration + expression replacement. | 0.5 days |
| 5 | `implement_missing_members` | Code insertion at @code block end. | 0.5 days |
| 6 | `change_signature` | Multi-file. Handles .razor callers via same reverse-map. | 1 day |
| 7 | `extract_method` | Most complex. Multiple insertions. | 1 day |

**Safety:** All start in `preview: true` only. Preview shows `.razor` diff (not generated C# diff). After validation, `preview: false` applies + regenerates virtual doc.

### Stream E — Type, Discovery, Code Action Tools (Week 5, Days 1-5)
**Owner: Dev 2 | 5 days**

**E1 — Type-based tools** (2 days):
Post-filter result locations from generated C# paths to `.razor` paths using `TranslateLocation`.

**E2 — Discovery tools** (1 day):
- `get_project_structure`: add `razorFileCount`, `isRazorProject`
- `get_di_registrations`: scan `@inject` directives in generated C#
- Others: works as-is or minimal adaptation

**E3 — Compound tools** (1 day): Inherit from adapted constituent tools.

**E4 — Code actions** (1 day): Run on virtual C# → translate edits to `.razor`.

### Phase F — sync_documents + Tag Helpers (Week 6, Days 1-3)
**Owner: Both devs | 2 days**

**F1 — sync_documents** (Dev 1, 1 day):
Extend `SyncDocumentsAsync` to handle `.razor`:
- Changed → re-process → update virtual doc
- Added → process → add virtual doc
- Deleted → remove from registry + remove virtual doc

**F2 — Tag helpers** (Dev 2, 1 day):
Use whatever API `Microsoft.CodeAnalysis.Razor` exposes for tag helper discovery. Fall back to basic assembly scanning if unavailable.

### Phase G — Testing & Ship (Week 6 Day 4 → Week 7 Day 5)
**Owner: Both devs | 6 days**

- Integration tests with real Blazor project fixture (2 days)
- Edge case tests: code-behind, no @code block, deeply nested C#, partial classes (2 days)
- Update README, CHANGELOG, server.json (0.5 days)
- Run full existing suite (543 tests) + new tests → fix regressions (1 day)
- PR review + merge (0.5 days)

---

## 8. Risks & Trade-offs

| Risk | Likelihood | Severity | Mitigation |
|---|---|---|---|
| **Microsoft.CodeAnalysis.Razor not compatible with net8.0** | Medium | Critical | Phase 0 verifies this FIRST. If incompatible, use `Microsoft.AspNetCore.Razor.Language` directly + custom thin mapping layer. Adds ~2 weeks. |
| **High-level services (RazorDocumentMappingService) are internal/private** | Medium | Medium | Fall back to direct `RazorCSharpDocument.SourceMappings` usage — this is what the internal services use anyway. Adds ~3 days for custom mapping helpers. |
| **Tag helper APIs are internal** | High | Low | Basic tag helper support is not critical for Phase 1. `@code` blocks and simple components work without it. Add later. |
| **Source mapping accuracy at edge cases** | Low | Medium | Phase 0 spike tests complex constructs: `@bind:event`, nested lambdas, `@foreach` with complex expressions. |
| **Reverse mapping refactoring edits breaks .razor** | Medium | Critical | Preview-only initially. Extensive test suite. Snapshot/restore pattern. After apply, re-process .razor and verify compilation. |
| **Phase 0 spike reveals unexpected blockers** | Low | High | The spike IS the gate. No commitment to full implementation before spike passes. |
| **Generated C# doesn't compile against real project references** | Low | Medium | `RazorProjectEngine` needs proper configuration (namespace, usings, base class). Default Blazor config works for standard components. |

### Revised Effort Summary

| Phase | Owner(s) | Duration | Down from v1 |
|---|---|---|---|
| 0 — Spike | Both | 3 days | — |
| A — Watcher | Dev 1 | 4 days | same |
| B — Core Infra | Both | 6 days | 8 days |
| C1 — Navigation | Dev 1 | 2 days | 4 days |
| C2 — Analysis | Dev 2 | 2 days | 4 days |
| D — Refactoring | Dev 1 | 4 days | 8 days |
| E — Type+Discovery+Actions | Dev 2 | 5 days | 7 days |
| F — sync_docs + Tag Helpers | Both | 2 days | 5 days |
| G — Testing | Both | 6 days | 6 days |
| **Total** | | **~5 weeks (2 devs)** | **~11 weeks** |

Single developer: **8-10 weeks** (down from 18-22).

---

## Appendix: File Changes Summary

### New files (14)
```
src/RazorFileInfo.cs
src/RazorService.cs                   — ProcessRazorFile, GetOrCreateRazorEngine, AddVirtualDoc
src/RazorService.Mapping.cs           — MapRazorToCSharp, MapCSharpToRazor, TranslateLocation
src/RazorService.TagHelpers.cs        — (if needed) Tag helper discovery
src/RoslynService.Watcher.cs          — FileSystemWatcher with debounce

tests/SharpLensMcp.Tests/
  RazorMappingTests.cs
  RazorNavigationTests.cs
  RazorAnalysisTests.cs
  RazorRefactoringTests.cs
  RazorIntegrationTests.cs
  WatcherTests.cs
  Fixtures/
    Counter.razor
    ComponentWithBind.razor
    ComponentWithEvent.razor
    GenericComponent.razor
    Simple.razor.cs                   — code-behind
```

### Modified files (11)
```
src/SharpLensMcp.csproj               — Add Microsoft.CodeAnalysis.Razor
src/RoslynService.cs                  — TryFindDocument: .razor routing
                                         LoadSolutionAsync: Razor detection
                                         sync_documents: .razor handling
src/RoslynService.Navigation.cs       — 9 tools: PrepareRazorAwareContext wrapper
src/RoslynService.Analysis.cs         — 6 tools: position translation
src/RoslynService.Refactoring.cs      — 7 tools: TranslateRefactoringToRazor
src/RoslynService.Compound.cs         — 5 tools: .razor path routing
src/RoslynService.TypeDiscovery.cs    — 5 tools: result path translation
src/RoslynService.Discovery.cs        — get_project_structure, get_di_registrations
src/RoslynService.CodeActions.cs      — 2 tools: position translation
src/RoslynService.Inspection.cs       — Include razor docs in scans
src/RoslynService.Quality.cs          — .razor path reporting
README.md / CHANGELOG.md / server.json
```

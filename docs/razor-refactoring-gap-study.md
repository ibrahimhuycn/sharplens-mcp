# Razor Refactoring — Gap Study

## What's deferred and why

Applying refactoring edits to `.razor` files requires reverse-mapping Roslyn's `TextChange` objects (which operate on generated C# character offsets) back to `.razor` character offsets. This is the single hardest problem in the Razor integration because:

1. Roslyn refactorings produce `Solution` changes with `TextChange` objects at specific character offsets in the target document
2. The target document is the **generated C#** (synthetic `.g.cs`), not the `.razor` file
3. Each `TextChange` span must be translated from generated C# offsets → `.razor` offsets using `SourceMapping`
4. Some generated C# has **no `.razor` equivalent** (`BuildRenderTree`, auto-generated capture lambdas)
5. After applying edits, the `.razor` file must be re-processed to regenerate the virtual C# document

### The edit flow

```
MCP client: rename_symbol("Counter.razor", line=1, col=9, newName="Foo")
    │
    ▼
PrepareRazorAwareContext→ offset translation → Renamer.RenameSymbolAsync on virtual doc
    │
    ▼
newSolution.GetChanges(_solution) → TextChange[] on generated C#
    │
    ▼
??? Translate TextChange spans back to .razor offsets ???
    │
    ▼
Apply text replacements to .razor on disk + regenerate virtual doc
```

## Tool-by-tool analysis

### 1. `rename_symbol` — **simplest, most valuable**

**Current state:** Preview works (shows `.razor` paths). Apply (`preview: false`) not yet implemented for `.razor`.

**What's needed:**
- After `Renamer.RenameSymbolAsync` returns `newSolution`, extract `TextChange` objects for each changed razor virtual document
- For each change: find the corresponding `SourceMapping` in the `RazorCSharpDocument`, reverse-map the span offsets
- Text changes are typically **single-identifier replacements** — one `TextChange` per occurrence
- Apply the text replacement to the `.razor` file on disk at the mapped offset
- Re-process the `.razor` file via `ProcessRazorFile` to regenerate the virtual doc

**Complexity:** **Medium**. Replacing identifiers is the simplest refactoring — no structural changes, just `oldIdentifier → newIdentifier` at known offsets.

**Estimated effort:** 3-4 days

### 2. `extract_method` — **complex, medium value**

**Current state:** Not adapted at all for `.razor`.

**What's needed:**
- Accept `.razor` position (currently uses `GetDocumentAsync` + `GetPosition` — needs `PrepareRazorAwareContext`)
- Run the extract method refactoring on the virtual C# document
- The refactoring produces TWO types of changes:
  - **Removal** of the extracted code block from the original location
  - **Insertion** of a new method at the end of the class body
- Both must be reverse-mapped to `.razor` `@code` block positions
- The new method insertion must land inside the `@code { }` block (not before/after)

**Complexity:** **High**. Multiple edit types, structural awareness needed, must respect `@code` block boundaries.

**Estimated effort:** 5-7 days

### 3. `change_signature` — **complex, medium value**

**Current state:** Not adapted.

**What's needed:**
- Multi-file refactoring — method signature changes in one file may affect callers in other `.razor` and `.cs` files
- Each changed document must be checked: is it a razor virtual doc? If so, reverse-map changes
- Parameter additions/removals/renames — edits may restructure code (not just identifiers)

**Complexity:** **High**. Cross-file, structural edits, parameter reordering affects call site syntax.

**Estimated effort:** 5-7 days

### 4. Other refactoring tools

| Tool | Effort | Notes |
|---|---|---|
| `encapsulate_field` | 2 days | Generate property in `@code` block |
| `inline_variable` | 1 day | Simple delete + paste |
| `extract_variable` | 1 day | Simple insert + replace |
| `implement_missing_members` | 2 days | Insert stubs at end of `@code` block |
| `generate_constructor` | 2 days | Insert constructor in `@code` block |
| `generate_equality_members` | 2 days | Insert members in `@code` block |
| `add_null_checks` | 1 day | Wrap parameters in guard clauses |

### 5. Other deferred items

#### Tag helper resolution
**What:** `<InputText @bind-Value="model.Name" />` should be recognized as a component, not literal HTML.
**Why deferred:** `Microsoft.CodeAnalysis.Razor` may not expose public tag helper APIs. Basic operation works without it — `@code` blocks and `@bind`/`@onclick` on primitive types resolve correctly.
**Estimated effort:** 3-5 days

#### `get_project_structure` razor file count
**What:** Should report `razorFileCount` and include `.razor` entries in the documents list.
**Why deferred:** Low priority. Virtual docs are visible in the document count but with generated `.g.cs` paths.
**Estimated effort:** 0.5 days

#### Multi-file integration test with real Blazor `.csproj`
**What:** Load a real Blazor project via MSBuildWorkspace, verify end-to-end.
**Why deferred:** Needs a test fixture project with proper Blazor SDK references. The in-memory tests cover the core pipeline.
**Estimated effort:** 2 days

#### `find_unused_code` exclude generated-only symbols
**What:** `BuildRenderTree`, auto-generated capture methods should be excluded from dead code results.
**Why deferred:** Low impact. Razor components rarely have unused code in `@code` blocks.
**Estimated effort:** 1 day

## Implementation priority

| Priority | Item | Reason |
|---|---|---|
| **P0** | `rename_symbol` apply for `.razor` | Most requested, simplest refactoring, clear user value |
| **P1** | `encapsulate_field`, `extract_variable`, `inline_variable` | Simple edits, low risk |
| **P2** | `implement_missing_members`, `generate_constructor` | Code generation, always inserts |
| **P3** | `extract_method` | Complex, structural awareness needed |
| **P4** | `change_signature` | Cross-file, highest complexity |
| **P5** | Tag helpers, project structure, integration tests | Polish and completeness |

## Technical approach for `rename_symbol` apply

The core algorithm for the reverse mapping:

```csharp
// For each razor virtual document that changed:
foreach (var docId in projectChanges.GetChangedDocuments())
{
    if (!IsRazorGeneratedDocument(docId)) continue;

    var oldDoc = _solution.GetDocument(docId);
    var newDoc = newSolution.GetDocument(docId);
    var textChanges = await newDoc.GetTextChangesAsync(oldDoc);
    
    var razorInfo = _razorDocuments.Values.First(r => r.VirtualDocumentId == docId);
    var razorText = razorInfo.RazorSourceText;

    foreach (var change in textChanges)
    {
        // Find the source mapping that covers this generated offset range
        var mapping = razorInfo.CSharpDocument.SourceMappings.FirstOrDefault(m =>
            change.Span.Start >= m.GeneratedSpan.AbsoluteIndex
            && change.Span.End <= m.GeneratedSpan.AbsoluteIndex + m.GeneratedSpan.Length);

        if (mapping == null) continue; // Generated-only code, skip

        // Map the entire span (not just start) using the fraction approach
        var startFrac = (double)(change.Span.Start - mapping.GeneratedSpan.AbsoluteIndex)
                      / mapping.GeneratedSpan.Length;
        var endFrac = (double)(change.Span.End - mapping.GeneratedSpan.AbsoluteIndex)
                    / mapping.GeneratedSpan.Length;

        var razorStart = mapping.OriginalSpan.AbsoluteIndex
                       + (int)(startFrac * mapping.OriginalSpan.Length);
        var razorEnd = mapping.OriginalSpan.AbsoluteIndex
                     + (int)(endFrac * mapping.OriginalSpan.Length);

        // Apply the replacement to the razor text
        razorText = razorText.Remove(razorStart, razorEnd - razorStart)
                             .Insert(razorStart, change.NewText);
    }

    // Write the updated text back to disk
    File.WriteAllText(razorInfo.RazorFilePath, razorText);

    // Re-process the razor file to regenerate the virtual doc
    var project = FindProjectForFile(razorInfo.RazorFilePath);
    ProcessRazorFile(razorInfo.RazorFilePath, project!);
}
```

**Key edge cases to handle:**
- Multiple changes in the same razor file (rename updates many occurrences)
- Changes that span source mapping boundaries (unlikely for identifiers, possible for code blocks)
- Changes in auto-generated code (BuildRenderTree, lambda captures) — skip with null check
- Case-sensitivity on identifier replacement
- Re-processing after apply: the `ProcessRazorFile` call regenerates the virtual C# document
- Snapshot/restore: snapshot `.razor` file before applying, restore on failure

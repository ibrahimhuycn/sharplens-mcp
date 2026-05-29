# AGENTS.md

SharpLensMcp — an MCP server providing 67 Roslyn-powered C# semantic analysis tools.

## Build, test, lint

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

No separate lint or formatter step. Tests are the CI gate (`dotnet test --configuration Release --no-build --verbosity normal`).

## Run a single test

```bash
dotnet test -c Release --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

Tests must run from inside the repo (they walk up from `Directory.GetCurrentDirectory()` to find `SharpLensMcp.sln`). Running from the project dir works; running from `/tmp` will fail.

## Architecture

```
MCP client (AI agent)
  → stdin/stdout JSON-RPC 2.0
  → McpServer.cs             — protocol dispatch, tool definitions, routing
  → RoslynService.cs         — partial class (core helpers, EnsureSolutionLoaded, response factories)
  → RoslynService.*.cs       — 11 partial files: Navigation, Analysis, Refactoring, TypeDiscovery,
                                 Discovery, ExternalApi, Quality, CodeActions, CodeGeneration,
                                 Compound, Inspection, Validation
```

Entry point: `Program.cs` — calls `MSBuildLocator.RegisterDefaults()` then `new McpServer().RunAsync()`.

**Key types**:
- `JsonRpcParameters` (struct) — typed argument access; missing/type errors throw `JsonRpcInvalidParamsException` → -32602 response.
- `ErrorCodes` (static) — `SOLUTION_NOT_LOADED`, `FILE_NOT_FOUND`, `SYMBOL_NOT_FOUND`, `INVALID_PARAMETER`, etc.
- `RequestId` (struct with JSON converter) — supports string or integer JSON-RPC ids per spec.
- `RoslynError`, `ResponseMetadata`, `SignatureChange`, `ConstructorMember`, `QualityAuditData` — typed data records.

## Test architecture (critical)

Two test styles, xUnit + FluentAssertions:

1. **Solution-loaded integration** (`RoslynServiceTestBase`): loads `SharpLensMcp.sln` via MSBuildWorkspace. Slow (~30s amortized). Used for tools needing real project references/generators.

2. **In-memory** (`TestHelpers.CreateWorkspaceWithCode` + `RoslynService.LoadFromWorkspaceForTesting`): AdhocWorkspace with hand-crafted code. ~5ms/test. For tools working on incomplete/broken code (`get_missing_members`, `validate_code`, `add_null_checks`).

**Test project**: `tests/SharpLensMcp.Tests/` references the main project via `InternalsVisibleTo`. It also references `SharpLensMcp.Tests.TestAnalyzers` (netstandard2.0, contains `DiagnosticAnalyzer` types loaded dynamically for analyzer diagnostics tests).

**Parsing tool responses in tests**: Use `Newtonsoft.Json.Linq.JObject.FromObject(response)`. Property names are PascalCase per the C# objects (`data["Code"]` not `data["code"]`, `success`, `data`).

### Test contract (from `tests/SharpLensMcp.Tests/TESTING.md`)

- Every test must assert a concrete value (name, count, substring, kind) — never shape-only.
- No `if (results?.Count > 0) { … }` silent-skip patterns.
- Response field name strings in tests must match the implementation's `CreateSuccessResponse` payload exactly (`data["results"]` vs `data["symbols"]` mismatch is a common bug).
- `AssertSuccess()` and `AssertError()` in `RoslynServiceTestBase` harden against null-conditional short-circuits.
- Tests that modify disk files must snapshot before and restore after in `DisposeAsync`.

## Adding a new tool

Follow the existing 4-step pattern (see README "Adding New Tools"):
1. Add method to the appropriate `RoslynService.*.cs` partial file.
2. Add tool definition in `McpServer.HandleListToolsAsync`.
3. Add routing in `McpServer.HandleToolCallAsync` switch (use `JsonRpcParameters` for argument access, never raw indexer).
4. Build + publish.

Response format: `CreateSuccessResponse(data: new { … }, suggestedNextTools: …)` or `CreateErrorResponse(ErrorCodes.*, …)`.

## Environment variables

| Variable | Default | Notes |
|---|---|---|
| `DOTNET_SOLUTION_PATH` | — | Path to `.sln`/`.slnx` or directory; auto-loaded at startup |
| `SHARPLENS_ABSOLUTE_PATHS` | `false` | Relative paths save LLM tokens |
| `ROSLYN_LOG_LEVEL` | `Information` | Trace/Debug/Information/Warning/Error |
| `ROSLYN_TIMEOUT_SECONDS` | `30` | Per-operation timeout |
| `ROSLYN_MAX_DIAGNOSTICS` | `100` | Max diagnostics returned |
| `ROSLYN_ENABLE_SEMANTIC_CACHE` | `true` | Semantic model caching |
| `SHARPLENS_WATCH_MODE` | `false` | Auto-sync documents on file changes (no manual `sync_documents` needed) |
| `SHARPLENS_WATCH_DEBOUNCE_MS` | `300` | Debounce window for filesystem watcher |
| `SHARPLENS_WATCH_EXTENSIONS` | `.cs,.razor,.cshtml` | Watched file extensions |

## Key constraints

- **.NET 10.0** required (targets `net10.0`).
- **Roslyn 5.3.0** + **MSBuildLocator 1.11.2** (must be registered before any Roslyn code runs).
- Npm wrapper (`npm/`) pins .NET tool version to `package.json version`.
- Both `.sln` and `.slnx` solution formats supported.
- Tools use `EnsureSolutionLoaded()` guard — auto-load from `DOTNET_SOLUTION_PATH` or require explicit `load_solution`.
- After external file edits (Write/Edit tools), `sync_documents` must be called. Refactoring tools auto-sync.

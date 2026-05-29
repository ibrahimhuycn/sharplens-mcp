using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

public class RazorNavigationIntegrationTests
{
    private static (string d, string p) Setup(string c)
    {
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, "C.razor");
        File.WriteAllText(p, c);
        return (d, p);
    }

    private static RoslynService Svc(string d)
    {
        var (ws, _) = TestHelpers.CreateWorkspaceWithCode("class P { }");
        var r = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.aspnetcore.components");
        if (Directory.Exists(r))
            foreach (var dir in Directory.GetDirectories(r).OrderByDescending(x => x))
            {
                var a = Path.Combine(dir, "lib", "net8.0", "Microsoft.AspNetCore.Components.dll");
                if (File.Exists(a))
                {
                    ws.TryApplyChanges(ws.CurrentSolution.AddMetadataReference(
                        ws.CurrentSolution.ProjectIds.Single(), MetadataReference.CreateFromFile(a)));
                    break;
                }
            }
        var svc = new RoslynService();
        svc.LoadFromWorkspaceForTesting(ws);
        svc.ProcessRazorFile(Path.Combine(d, "C.razor"), ws.CurrentSolution.Projects.Single());
        return svc;
    }

    // ---- GetSymbolInfo ----
    [Fact]
    public async Task GetSymbolInfo_OnField_ReturnsFieldWithRazorPath()
    {
        var code = "@code {\n    private int _counter;\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).GetSymbolInfoAsync(Path.Combine(d, "C.razor"), 1, 16);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.Equal("_counter", (string)j["data"]!["name"]!);
            Assert.Equal("Field", (string)j["data"]!["kind"]!);
            Assert.EndsWith(".razor", (string)j["data"]!["location"]!["filePath"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- FindReferences ----
    [Fact]
    public async Task FindReferences_OnField_FindsDeclAndUsage()
    {
        var code = "@code {\n    private int _c;\n    void Inc() { _c++; }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).FindReferencesAsync(Path.Combine(d, "C.razor"), 1, 16);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            var refs = (JArray)j["data"]!["references"]!;
            Assert.True(refs.Count >= 1, $"Got {refs.Count}");
            foreach (var x in refs)
                Assert.EndsWith(".razor", (string)x["filePath"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- GoToDefinition ----
    [Fact]
    public async Task GoToDefinition_OnCall_JumpsToRazorDecl()
    {
        var code = "@code {\n    void M() { }\n    void C() { M(); }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).GoToDefinitionAsync(Path.Combine(d, "C.razor"), 2, 15);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            var def = j["data"]!["definition"]!;
            Assert.EndsWith(".razor", (string)def["filePath"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- FindCallers ----
    [Fact]
    public async Task FindCallers_OnMethod_FindsCallerInRazor()
    {
        var code = "@code {\n    void T() { }\n    void C() { T(); }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).FindCallersAsync(Path.Combine(d, "C.razor"), 1, 9);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.NotEmpty((JArray)j["data"]!["callers"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- GetContainingMember ----
    [Fact]
    public async Task GetContainingMember_InMethodBody_ReturnsMethod()
    {
        var code = "@code {\n    void M() { int x = 1; }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).GetContainingMemberAsync(Path.Combine(d, "C.razor"), 1, 19);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.Equal("M", (string)j["data"]!["memberName"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- GetOutgoingCalls ----
    [Fact]
    public async Task GetOutgoingCalls_FindsCalleeInRazor()
    {
        var code = "@code {\n    void H() { }\n    void M() { H(); }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).GetOutgoingCallsAsync(Path.Combine(d, "C.razor"), 2, 9);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.NotEmpty((JArray)j["data"]!["calls"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- Error case ----
    [Fact]
    public async Task PositionInMarkup_ReturnsError()
    {
        var (d, _) = Setup("<h1>Hello</h1>");
        try
        {
            var r = await Svc(d).GetSymbolInfoAsync(Path.Combine(d, "C.razor"), 0, 2);
            Assert.False((bool)JObject.FromObject(r)["success"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- SyncDocuments preserves razor tracking ----
    [Fact]
    public async Task SyncDocuments_DoesNotDropRazorTracking()
    {
        var code = "@code {\n    private int _x;\n}";
        var (d, razorPath) = Setup(code);
        try
        {
            var svc = Svc(d);
            // Verify razor file is accessible before sync
            var docBefore = svc.TryFindDocument(razorPath);
            Assert.NotNull(docBefore);

            // Run sync (no explicit files — syncs all)
            var syncResult = await svc.SyncDocumentsAsync(null);
            var syncJ = JObject.FromObject(syncResult);
            Assert.True((bool)syncJ["success"]!, $"Sync failed: {syncJ["error"]}");

            // Verify razor file is STILL accessible after sync
            var docAfter = svc.TryFindDocument(razorPath);
            Assert.NotNull(docAfter);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- GetTypeHierarchy on field position resolves to containing type ----
    [Fact]
    public async Task GetTypeHierarchy_OnFieldInCodeBlock_ResolvesToType()
    {
        var code = "@code {\n    private int _x;\n}";
        var (d, _) = Setup(code);
        try
        {
            // Position on the field declaration — should resolve upwards to the containing class
            var r = await Svc(d).GetTypeHierarchyAsync(Path.Combine(d, "C.razor"), 1, 16);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            var firstName = (string)j["data"]!["baseTypes"]![0]!["name"]!;
            Assert.Contains("ComponentBase", firstName);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    // ---- Auto-discovery of .razor files on first access ----
    [Fact]
    public async Task GetSymbolInfo_UndiscoveredRazorFile_AutoDiscovers()
    {
        // Place the .razor file inside the project directory so FindProjectForFile can locate it.
        // CreateWorkspaceWithCode puts documents under adhoc-{guid}/, so use that base dir.
        var (ws, doc) = TestHelpers.CreateWorkspaceWithCode("class P { }");
        var projectDir = Path.GetDirectoryName(doc.FilePath)!;
        Directory.CreateDirectory(projectDir); // CreateWorkspaceWithCode only sets a virtual path
        var razorPath = Path.Combine(projectDir, "Page.razor");
        File.WriteAllText(razorPath, "@code {\n    private string _name;\n}");
        try
        {
            var r = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages", "microsoft.aspnetcore.components");
            if (Directory.Exists(r))
                foreach (var dir in Directory.GetDirectories(r).OrderByDescending(x => x))
                {
                    var a = Path.Combine(dir, "lib", "net8.0", "Microsoft.AspNetCore.Components.dll");
                    if (File.Exists(a))
                    {
                        ws.TryApplyChanges(ws.CurrentSolution.AddMetadataReference(
                            ws.CurrentSolution.ProjectIds.Single(), MetadataReference.CreateFromFile(a)));
                        break;
                    }
                }
            var svc = new RoslynService();
            svc.LoadFromWorkspaceForTesting(ws);
            // NO call to ProcessRazorFile — simulate first-touch discovery

            var result = await svc.GetSymbolInfoAsync(razorPath, 1, 20);
            var j = JObject.FromObject(result);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.Equal("_name", (string)j["data"]!["name"]!);
        }
        finally { try { File.Delete(razorPath); } catch { } }
    }
}

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
}

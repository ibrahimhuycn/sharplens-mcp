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

    [Fact]
    public async Task GetSymbolInfo_OnRazorField_ReturnsSymbol()
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

    [Fact]
    public async Task GetOutgoingCalls_FindsCallees()
    {
        var code = "@code {\n    void H() { }\n    void M() { H(); }\n}";
        var (d, _) = Setup(code);
        try
        {
            var r = await Svc(d).GetOutgoingCallsAsync(Path.Combine(d, "C.razor"), 2, 15);
            var j = JObject.FromObject(r);
            Assert.True((bool)j["success"]!, $"Err: {j["error"]}");
            Assert.NotEmpty((JArray)j["data"]!["calls"]!);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

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

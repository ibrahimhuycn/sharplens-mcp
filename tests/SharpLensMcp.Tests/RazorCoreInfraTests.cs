using Microsoft.CodeAnalysis;
using Xunit;

namespace SharpLensMcp.Tests;

public class RazorCoreInfraTests
{
    // ---- IsRazorProject ----

    [Fact]
    public void IsRazorProject_WithComponentsRef_ReturnsTrue()
    {
        var compPath = FindCompAssembly();
        var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class C { }");
        var solution = ws.CurrentSolution.AddMetadataReference(
            ws.CurrentSolution.ProjectIds.Single(),
            MetadataReference.CreateFromFile(compPath));
        ws.TryApplyChanges(solution);
        Assert.True(RoslynService.IsRazorProject(ws.CurrentSolution.Projects.Single()));
    }

    [Fact]
    public void IsRazorProject_WithoutComponentsRef_ReturnsFalse()
    {
        var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class C { }");
        Assert.False(RoslynService.IsRazorProject(ws.CurrentSolution.Projects.Single()));
    }

    // ---- ProcessRazorFile ----

    [Fact]
    public void ProcessRazorFile_GeneratesCSharp()
    {
        var (tempDir, razorPath) = WriteTempRazor("@code { private int _x; }");
        try
        {
            var svc = new RoslynService();
            var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class X { }");
            svc.LoadFromWorkspaceForTesting(ws);
            var info = svc.ProcessRazorFile(razorPath, ws.CurrentSolution.Projects.Single());
            Assert.Contains("_x", info.GeneratedSourceText);
            Assert.NotEmpty(info.CSharpDocument.SourceMappings);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // ---- TryFindDocument .razor routing ----

    [Fact]
    public void TryFindDocument_RazorFile_ReturnsVirtualDoc()
    {
        var (tempDir, razorPath) = WriteTempRazor("@code { private int _f; }");
        try
        {
            var svc = new RoslynService();
            var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class X { }");
            svc.LoadFromWorkspaceForTesting(ws);
            svc.ProcessRazorFile(razorPath, ws.CurrentSolution.Projects.Single());
            // Must use absolute path to match the cache key
            Assert.NotNull(svc.TryFindDocument(razorPath));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // ---- MapRazorPositionToCSharp ----

    [Fact]
    public void MapRazorPositionToCSharp_Field_MapsCorrectly()
    {
        var (tempDir, razorPath) = WriteTempRazor("@code {\n    private int _ctr;\n}");
        try
        {
            var svc = new RoslynService();
            var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class X { }");
            svc.LoadFromWorkspaceForTesting(ws);
            svc.ProcessRazorFile(razorPath, ws.CurrentSolution.Projects.Single());
            var result = svc.MapRazorPositionToCSharp(razorPath, 1, 16);
            Assert.NotNull(result);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [Fact]
    public void MapRazorPositionToCSharp_Markup_ReturnsNull()
    {
        var (tempDir, razorPath) = WriteTempRazor("<h1>Hello</h1>");
        try
        {
            var svc = new RoslynService();
            var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class X { }");
            svc.LoadFromWorkspaceForTesting(ws);
            svc.ProcessRazorFile(razorPath, ws.CurrentSolution.Projects.Single());
            Assert.Null(svc.MapRazorPositionToCSharp(razorPath, 0, 2));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // ---- IsRazorGeneratedDocument ----

    [Fact]
    public void IsRazorGeneratedDocument_VirtualDoc_True()
    {
        var (tempDir, razorPath) = WriteTempRazor("@code { private int _x; }");
        try
        {
            var svc = new RoslynService();
            var (ws, _) = TestHelpers.CreateWorkspaceWithCode("public class X { }");
            svc.LoadFromWorkspaceForTesting(ws);
            svc.ProcessRazorFile(razorPath, ws.CurrentSolution.Projects.Single());
            var doc = svc.TryFindDocument(razorPath);
            Assert.True(svc.IsRazorGeneratedDocument(doc!));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // ---- GetOffset / GetLineColumn ----

    [Theory]
    [InlineData("a\nb\nc", 0, 0, 0)]
    [InlineData("a\nb\nc", 1, 0, 2)]
    [InlineData("a\nb\nc", 2, 0, 4)]
    public void GetOffset(string t, int l, int c, int e) => Assert.Equal(e, RoslynService.GetOffset(t, l, c));

    [Theory]
    [InlineData("a\nb\nc", 0, 0, 0)]
    [InlineData("a\nb\nc", 2, 1, 0)]
    [InlineData("a\nb\nc", 4, 2, 0)]
    public void GetLineColumn(string t, int o, int el, int ec)
    {
        var (l, c) = RoslynService.GetLineColumn(t, o);
        Assert.Equal(el, l);
        Assert.Equal(ec, c);
    }

    // ---- Helpers ----

    private static (string dir, string razorPath) WriteTempRazor(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Test.razor");
        File.WriteAllText(path, content);
        return (dir, path);
    }

    private static string FindCompAssembly()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.aspnetcore.components");
        if (!Directory.Exists(root)) return typeof(object).Assembly.Location;
        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
        {
            var asm = Path.Combine(dir, "lib", "net8.0", "Microsoft.AspNetCore.Components.dll");
            if (File.Exists(asm)) return asm;
        }
        return typeof(object).Assembly.Location;
    }
}

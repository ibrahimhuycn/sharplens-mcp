using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SharpLensMcp.Tests;

public class RazorPipelineSpikeTests : IDisposable
{
    private readonly string _tempDir;

    public RazorPipelineSpikeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"razor-spike-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Spike_ProcessCounter_GeneratesCSharp()
    {
        var csharp = ProcessRazor("@code { private int _counter; }", "C.razor");
        Assert.Contains("_counter", csharp.GeneratedCode);
        Assert.NotEmpty(csharp.SourceMappings);
    }

    [Fact]
    public void Spike_RoundTripMapping()
    {
        var source = "@code { private int _counter; }";
        var csharp = ProcessRazor(source, "C.razor");
        var razorOffset = source.IndexOf("_counter");
        var mapping = csharp.SourceMappings.First(m =>
            razorOffset >= m.OriginalSpan.AbsoluteIndex
            && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);
        var frac = (double)(razorOffset - mapping.OriginalSpan.AbsoluteIndex) / mapping.OriginalSpan.Length;
        var genOff = mapping.GeneratedSpan.AbsoluteIndex + (int)(frac * mapping.GeneratedSpan.Length);
        var rFrac = (double)(genOff - mapping.GeneratedSpan.AbsoluteIndex) / mapping.GeneratedSpan.Length;
        var revOff = mapping.OriginalSpan.AbsoluteIndex + (int)(rFrac * mapping.OriginalSpan.Length);
        Assert.InRange(Math.Abs(revOff - razorOffset), 0, 2);
    }

    [Theory]
    [InlineData("@code { private int _x; }", "_x", SymbolKind.Field)]
    [InlineData("@code { private void M() { } }", "M", SymbolKind.Method)]
    [InlineData("@code { public string P { get; set; } }", "P", SymbolKind.Property)]
    public void Spike_ResolveSymbol(string razor, string id, SymbolKind kind)
    {
        var csharp = ProcessRazor(razor, "T.razor");
        var tree = CSharpSyntaxTree.ParseText(csharp.GeneratedCode);
        var comp = CSharpCompilation.Create("A", new[] { tree }, CoreRefs(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);

        // Find declaration using the right node type for each symbol kind
        var root = tree.GetRoot();
        ISymbol? sym = null;

        if (kind == SymbolKind.Field)
        {
            var v = root.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(d => d.Identifier.Text == id);
            sym = v != null ? model.GetDeclaredSymbol(v) : null;
        }
        else if (kind == SymbolKind.Method)
        {
            // Filter out generated methods (BuildRenderTree) — only match user methods
            var m = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(d => d.Identifier.Text == id
                    && d.Identifier.Text != "BuildRenderTree");
            sym = m != null ? model.GetDeclaredSymbol(m) : null;
        }
        else if (kind == SymbolKind.Property)
        {
            var p = root.DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(d => d.Identifier.Text == id);
            sym = p != null ? model.GetDeclaredSymbol(p) : null;
        }

        Assert.NotNull(sym);
        Assert.Equal(id, sym!.Name);
        Assert.Equal(kind, sym.Kind);
    }

    [Fact]
    public void Spike_PureMarkupNoMapping()
    {
        var source = "<h1>Hello</h1>";
        var csharp = ProcessRazor(source, "P.razor");
        var map = csharp.SourceMappings.FirstOrDefault(m =>
            source.IndexOf("<h1>") >= m.OriginalSpan.AbsoluteIndex
            && source.IndexOf("<h1>") < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);
        Assert.Null(map);
    }

    [Fact]
    public void Spike_TwentyFilesFast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            ProcessRazor($"@code {{ private int _f{i}; }}", $"F{i}.razor");
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    private RazorCSharpDocument ProcessRazor(string source, string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, source);
        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            RazorProjectFileSystem.Create(_tempDir),
            b => b.SetNamespace("TestApp"));
        return engine.Process(engine.FileSystem.GetItem(path)).GetCSharpDocument();
    }

    private static string? FindCompAsm()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.aspnetcore.components");
        if (!Directory.Exists(root)) return null;
        // Find the newest version that has a net8.0 TFM
        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
        {
            var asm = Path.Combine(dir, "lib", "net8.0", "Microsoft.AspNetCore.Components.dll");
            if (File.Exists(asm)) return asm;
        }
        return null;
    }

    private static MetadataReference[] CoreRefs()
    {
        var compAsm = FindCompAsm() ?? typeof(object).Assembly.Location;
        return new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(compAsm),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
    }
}

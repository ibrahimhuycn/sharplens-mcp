using Microsoft.AspNetCore.Razor.Language;
using Xunit;
using Xunit.Abstractions;

namespace SharpLensMcp.Tests;

public class RazorDebugMappingTests
{
    private readonly ITestOutputHelper _o;
    public RazorDebugMappingTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Debug_ShowMapping()
    {
        var code = "@code {\n    void M() { }\n    void C() { M(); }\n}";
        var d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, "Test.razor");
        File.WriteAllText(p, code);

        var eng = RazorProjectEngine.Create(RazorConfiguration.Default,
            RazorProjectFileSystem.Create(d), b => b.SetNamespace("Test"));
        var cd = eng.Process(eng.FileSystem.GetItem(p)).GetCSharpDocument();

        _o.WriteLine("=== Generated C# ===");
        _o.WriteLine(cd.GeneratedCode);
        _o.WriteLine("=== SourceMappings ===");
        foreach (var m in cd.SourceMappings)
            _o.WriteLine($"  Razor[{m.OriginalSpan.AbsoluteIndex}..{m.OriginalSpan.AbsoluteIndex+m.OriginalSpan.Length}] -> Gen[{m.GeneratedSpan.AbsoluteIndex}..{m.GeneratedSpan.AbsoluteIndex+m.GeneratedSpan.Length}]");

        // Position of M in M() — line 2 col 16
        // Line 2: "    void C() { M(); }" — M is at position... 
        // 4 spaces + "void C() { " = 16 chars, 'M' at col 16
        var razorOffset = RoslynService.GetOffset(code, 2, 16);
        _o.WriteLine($"\nRazor offset for (2,16) = {razorOffset}, char = '{code[razorOffset]}'");

        foreach (var m in cd.SourceMappings)
        {
            if (razorOffset >= m.OriginalSpan.AbsoluteIndex
                && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length)
            {
                var frac = (double)(razorOffset - m.OriginalSpan.AbsoluteIndex) / m.OriginalSpan.Length;
                var goff = m.GeneratedSpan.AbsoluteIndex + (int)(frac * m.GeneratedSpan.Length);
                _o.WriteLine($"Match: Razor[{m.OriginalSpan.AbsoluteIndex}..{m.OriginalSpan.AbsoluteIndex+m.OriginalSpan.Length}] -> Gen[{m.GeneratedSpan.AbsoluteIndex}..], genOff={goff}");
                _o.WriteLine($"  Gen char at offset: '{cd.GeneratedCode[goff]}'");
                _o.WriteLine($"  Gen context: '{cd.GeneratedCode.Substring(Math.Max(0,goff-20), Math.Min(60, cd.GeneratedCode.Length-goff+20))}'");
            }
        }

        try { Directory.Delete(d, true); } catch { }
    }
}

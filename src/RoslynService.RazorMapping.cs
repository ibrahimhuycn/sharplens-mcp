using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Position translation between .razor files and generated C# virtual documents.
// Uses RazorCSharpDocument.SourceMappings for character-level accuracy.
public partial class RoslynService
{
    /// <summary>
    /// Shared context for tools that operate on a document at a position.
    /// Handles .razor position translation transparently.
    /// </summary>
    internal record struct RazorContext(Document Document, int Offset, bool IsRazor, string? RazorPath);

    /// <summary>
    /// Prepare a razor-aware execution context. If the file is .razor, translates
    /// the position to a character offset in the generated C# virtual document.
    /// Returns an error object if the position is in markup or the file is not found.
    /// Use <c>ctx.Document.GetSyntaxTreeAsync().GetRoot().FindToken(ctx.Offset)</c>
    /// instead of GetPosition + FindToken.
    /// </summary>
    internal async Task<object?> PrepareRazorAwareContext(string filePath, int line, int column)
    {
        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                context: new { filePath });
        }

        if (!filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null)
                return CreateErrorResponse(ErrorCodes.AnalysisFailed, "No syntax tree");
            var offset = GetPosition(syntaxTree, line, column);
            return new RazorContext(document, offset, false, null);
        }

        var mapped = MapRazorPositionToOffset(filePath, line, column);
        if (mapped == null)
            return CreateErrorResponse(ErrorCodes.InvalidParameter,
                $"Position (line {line}, col {column}) has no C# equivalent. " +
                "Try a position inside an @code { } block, inline C# expression (@expr), " +
                "or an event handler value (OnClick=\"Handler\"). " +
                "For component tag names, use get_symbol_info with the type name instead.",
                context: new { filePath, line, column });

        return new RazorContext(mapped.Value.Doc, mapped.Value.Offset, true, filePath);
    }

    /// <summary>
    /// Map a line/column in .razor to a character offset in the generated C# document.
    /// If the exact position falls in markup, searches nearby for the nearest C#-mapped
    /// position (e.g., the method name inside <c>OnClick="SaveDraft"</c>). This makes
    /// position-based tools tolerant to approximate agent-provided cursor positions.
    /// Returns null only if no mapping exists within the search window.
    /// </summary>
    internal (Microsoft.CodeAnalysis.Document Doc, int Offset)? MapRazorPositionToOffset(
        string razorFilePath, int razorLine, int razorColumn)
    {
        var info = GetRazorFileInfo(razorFilePath);
        if (info == null) return null;

        var razorOffset = GetOffset(info.RazorSourceText, razorLine, razorColumn);
        if (razorOffset < 0 || razorOffset >= info.RazorSourceText.Length)
            return null;

        // Exact match
        var mapping = info.CSharpDocument.SourceMappings.FirstOrDefault(m =>
            razorOffset >= m.OriginalSpan.AbsoluteIndex
            && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);

        // Lenient fallback: search ±256 chars for nearest SourceMapping.
        // Measures distance to the NEAREST EDGE of each mapping span, not
        // just the start — avoids preferring large-span mappings whose start
        // is far away even when the target is inside them.
        if (mapping == null)
        {
            const int window = 256;
            var start = Math.Max(0, razorOffset - window);
            var end = Math.Min(info.RazorSourceText.Length, razorOffset + window);
            SourceMapping? best = null;
            int bestDist = int.MaxValue;
            foreach (var m in info.CSharpDocument.SourceMappings)
            {
                var mStart = m.OriginalSpan.AbsoluteIndex;
                var mEnd = mStart + m.OriginalSpan.Length;
                if (mEnd < start || mStart > end) continue; // outside window

                // Distance to nearest edge of the mapping
                int dist = razorOffset < mStart ? mStart - razorOffset
                         : razorOffset > mEnd ? razorOffset - mEnd
                         : 0;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                    if (dist == 0) break; // inside a mapping — optimal match
                }
            }
            mapping = best;
        }

        if (mapping == null) return null;

        var fraction = (double)(razorOffset - mapping.OriginalSpan.AbsoluteIndex)
                     / Math.Max(1, mapping.OriginalSpan.Length);
        var generatedOffset = mapping.GeneratedSpan.AbsoluteIndex
                            + (int)(fraction * mapping.GeneratedSpan.Length);

        var doc = _solution!.GetDocument(info.VirtualDocumentId);
        return doc != null ? (doc, generatedOffset) : null;
    }

    /// <summary>
    /// Map a line/column position in a .razor file to the equivalent position
    /// in the generated C# virtual document. Returns null if the position is
    /// in pure markup (no C# equivalent).
    /// </summary>
    internal (Document Document, int Line, int Column)? MapRazorPositionToCSharp(
        string razorFilePath, int razorLine, int razorColumn)
    {
        var info = GetRazorFileInfo(razorFilePath);
        if (info == null) return null;

        var razorOffset = GetOffset(info.RazorSourceText, razorLine, razorColumn);
        if (razorOffset < 0 || razorOffset >= info.RazorSourceText.Length)
            return null;

        var mapping = info.CSharpDocument.SourceMappings.FirstOrDefault(m =>
            razorOffset >= m.OriginalSpan.AbsoluteIndex
            && razorOffset < m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length);

        if (mapping == null) return null;

        var fraction = (double)(razorOffset - mapping.OriginalSpan.AbsoluteIndex)
                     / mapping.OriginalSpan.Length;
        var generatedOffset = mapping.GeneratedSpan.AbsoluteIndex
                            + (int)(fraction * mapping.GeneratedSpan.Length);

        var (line, col) = GetLineColumn(info.GeneratedSourceText, generatedOffset);
        var doc = _solution!.GetDocument(info.VirtualDocumentId);

        return doc != null ? (doc, line, col) : null;
    }

    /// <summary>
    /// Map a position in generated C# back to the source .razor file.
    /// Returns null if the position is in generated-only code (BuildRenderTree, etc.)
    /// or if the document is not a razor virtual document.
    /// </summary>
    internal (string FilePath, int Line, int Column)? MapCSharpPositionToRazor(
        Document generatedDoc, int csharpLine, int csharpColumn)
    {
        var info = _razorDocuments.Values.FirstOrDefault(r =>
            r != null && r.VirtualDocumentId.Equals(generatedDoc.Id));
        if (info == null) return null;

        var generatedOffset = GetOffset(info.GeneratedSourceText, csharpLine, csharpColumn);
        if (generatedOffset < 0 || generatedOffset >= info.GeneratedSourceText.Length)
            return null;

        var mapping = info.CSharpDocument.SourceMappings.FirstOrDefault(m =>
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

    /// <summary>
    /// Translate a Roslyn Location to a user-facing file path and line/column.
    /// If the location is in a razor-generated virtual document, maps to the
    /// original .razor file. Otherwise returns the location as-is (formatted).
    /// </summary>
    internal (string FilePath, int Line, int Column, int EndLine, int EndColumn)
        TranslateLocation(Location location)
    {
        if (location.SourceTree == null)
            return ("", 0, 0, 0, 0);

        var lineSpan = location.GetLineSpan();

        // Path already mapped by #line directive — return as-is
        var path = lineSpan.Path ?? "";

        var doc = _solution!.GetDocument(location.SourceTree);
        if (doc != null)
        {
            var razorMapping = MapCSharpPositionToRazor(
                doc,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character);

            if (razorMapping != null)
            {
                return (FormatPath(razorMapping.Value.FilePath),
                        razorMapping.Value.Line,
                        razorMapping.Value.Column,
                        razorMapping.Value.Line,
                        razorMapping.Value.Column);
            }
        }

        // #line directives didn't produce a .razor path — try heuristic for
        // Razor Source Generator output (e.g., Components_Pages_Foo_razor.g.cs)
        if (!string.IsNullOrEmpty(path) && path.Contains("RazorSourceGenerator", StringComparison.Ordinal))
        {
            var derived = TryDeriveRazorPathFromGenerated(path);
            if (derived != null)
            {
                return (FormatPath(derived),
                        lineSpan.StartLinePosition.Line,
                        lineSpan.StartLinePosition.Character,
                        lineSpan.EndLinePosition.Line,
                        lineSpan.EndLinePosition.Character);
            }
        }

        return (FormatPath(path),
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    /// <summary>
    /// Derive the original .razor file path from a Razor Source Generator .g.cs path.
    /// E.g., .../RazorSourceGenerator/Components_Pages_Foo_razor.g.cs → Components/Pages/Foo.razor
    /// This handles #line-less infrastructure code (EventCallback.Factory.Create, TypeInference).
    /// </summary>
    private static string? TryDeriveRazorPathFromGenerated(string generatedPath)
    {
        // Find the RazorSourceGenerator segment
        var idx = generatedPath.LastIndexOf("RazorSourceGenerator", StringComparison.Ordinal);
        if (idx < 0) return null;

        var afterSlash = generatedPath.IndexOf('/', idx);
        var fileName = afterSlash >= 0 && afterSlash + 1 < generatedPath.Length
            ? generatedPath[(afterSlash + 1)..]
            : Path.GetFileName(generatedPath);

        // Strip _razor.g.cs suffix → Components_Pages_Foo (or just Foo for root-folder)
        var core = fileName;
        if (core.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase))
            core = core[..^11];

        // Underscores → directory separators, last segment is the file name
        var segments = core.Split('_');

        // Root-folder component: single segment (e.g., ReceptionDataForm_razor.g.cs)
        if (segments.Length == 1)
            return segments[0] + ".razor";

        if (segments.Length < 2) return null;

        var dirParts = segments.Take(segments.Length - 1);
        var fileNamePart = segments.Last();

        return string.Join("/", dirParts) + "/" + fileNamePart + ".razor";
    }

    /// <summary>
    /// Translate a Roslyn Location, returning just the file path and start position.
    /// Convenience overload for tools that don't need end position.
    /// </summary>
    internal (string FilePath, int Line, int Column) TranslateLocationSimple(Location location)
    {
        var result = TranslateLocation(location);
        return (result.FilePath, result.Line, result.Column);
    }

    // ---- Offset/line-column helpers ----

    internal static int GetOffset(string text, int zeroBasedLine, int zeroBasedColumn)
    {
        var line = 0;
        var offset = 0;
        while (offset < text.Length && line < zeroBasedLine)
        {
            if (text[offset] == '\n') line++;
            offset++;
        }
        return Math.Min(offset + zeroBasedColumn, text.Length);
    }

    internal static (int line, int column) GetLineColumn(string text, int offset)
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

    private static int GetLineCount(string text)
    {
        var count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        return count;
    }

    /// <summary>
    /// Translate a .razor line range to the equivalent line range in the generated
    /// C# virtual document. This enables tools like analyze_data_flow and
    /// analyze_control_flow to work on .razor files by first mapping the range
    /// to C# coordinates, running the analysis there, then reporting results
    /// (which TranslateLocation maps back to .razor positions automatically).
    /// </summary>
    internal (Document Document, int StartLine, int EndLine)? MapRazorLineRangeToCSharp(
        string razorFilePath, int razorStartLine, int razorEndLine)
    {
        var info = GetRazorFileInfo(razorFilePath);
        if (info == null) return null;

        var razorText = info.RazorSourceText;
        var razorLineCount = GetLineCount(razorText);

        // Convert .razor lines to character offsets
        var razorStartOffset = Math.Min(GetOffset(razorText, razorStartLine, 0), razorText.Length);
        var razorEndOffset = razorEndLine >= razorLineCount - 1
            ? razorText.Length
            : Math.Min(GetOffset(razorText, razorEndLine + 1, 0), razorText.Length);

        // Find source mappings that overlap with the requested range
        var relevantMappings = info.CSharpDocument.SourceMappings
            .Where(m => m.OriginalSpan.AbsoluteIndex < razorEndOffset
                     && m.OriginalSpan.AbsoluteIndex + m.OriginalSpan.Length > razorStartOffset)
            .ToList();

        if (relevantMappings.Count == 0) return null;

        var genStartOffset = relevantMappings.First().GeneratedSpan.AbsoluteIndex;
        var lastMapping = relevantMappings.Last();
        var genEndOffset = lastMapping.GeneratedSpan.AbsoluteIndex + lastMapping.GeneratedSpan.Length;

        var (genStartLine, _) = GetLineColumn(info.GeneratedSourceText, genStartOffset);
        var (genEndLine, _) = GetLineColumn(info.GeneratedSourceText,
            Math.Min(genEndOffset, info.GeneratedSourceText.Length - 1));

        var doc = _solution!.GetDocument(info.VirtualDocumentId);
        return doc != null ? (doc, genStartLine, genEndLine) : null;
    }
}

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
                $"Position (line {line}, col {column}) is in Razor markup, not C# code. " +
                "Use a position inside an @code { } block or inline C# expression (@expr).",
                context: new { filePath, line, column });

        return new RazorContext(mapped.Value.Doc, mapped.Value.Offset, true, filePath);
    }

    /// <summary>
    /// Map a line/column in .razor to a character offset in the generated C# document.
    /// Returns null if the position is in pure markup.
    /// </summary>
    internal (Microsoft.CodeAnalysis.Document Doc, int Offset)? MapRazorPositionToOffset(
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
            r != null && r.VirtualDocumentId == generatedDoc.Id);
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

        var doc = _solution!.GetDocument(location.SourceTree);
        if (doc == null)
        {
            var span = location.GetLineSpan();
            return (FormatPath(span.Path),
                    span.StartLinePosition.Line,
                    span.StartLinePosition.Character,
                    span.EndLinePosition.Line,
                    span.EndLinePosition.Character);
        }

        var razorMapping = MapCSharpPositionToRazor(
            doc,
            location.GetLineSpan().StartLinePosition.Line,
            location.GetLineSpan().StartLinePosition.Character);

        if (razorMapping != null)
        {
            return (FormatPath(razorMapping.Value.FilePath),
                    razorMapping.Value.Line,
                    razorMapping.Value.Column,
                    razorMapping.Value.Line,
                    razorMapping.Value.Column);
        }

        var lineSpan = location.GetLineSpan();
        return (FormatPath(lineSpan.Path),
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
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
}

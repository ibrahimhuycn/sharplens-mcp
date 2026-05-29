using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

internal sealed record RazorFileInfo
{
    public required string RazorFilePath { get; init; }
    public required DocumentId VirtualDocumentId { get; init; }
    public required string RazorSourceText { get; init; }
    public required string GeneratedSourceText { get; init; }
    public required RazorCSharpDocument CSharpDocument { get; init; }
    public required DateTime ProcessedAt { get; init; }
}

namespace PdfFormFiller;

/// <summary>Read-only description of a form field.</summary>
public sealed record FormFieldInfo(
    string Name,
    FieldType Type,
    string? Value,
    IReadOnlyList<string> Options,
    IReadOnlyList<int> Pages,
    bool ReadOnly,
    bool Required,
    bool MultiLine);

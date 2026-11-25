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
    bool MultiLine)
{
    /// <summary>
    /// Display texts for <see cref="Options"/>, same length and order (<c>OptionTexts[i]</c> belongs to <c>Options[i]</c>).
    /// For combo and list boxes this is the visible text of each /Opt entry, for radio buttons the /Opt text of the
    /// button. Where the PDF carries no separate display text (check boxes, options given as plain strings) the text is
    /// the export value itself. Objects built through the constructor default to the export values.
    /// </summary>
    public IReadOnlyList<string> OptionTexts { get; init; } = Options;
}

namespace PdfFormFiller;

/// <summary>Base type for every error the library reports: unreadable PDFs, wrong field types, unknown options.</summary>
public class PdfFormException : Exception
{
    public PdfFormException() { }
    public PdfFormException(string message) : base(message) { }
    public PdfFormException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A field name passed to the API does not exist in the document.</summary>
public sealed class FieldNotFoundException : PdfFormException
{
    public string FieldName { get; } = string.Empty;

    public FieldNotFoundException() { }

    public FieldNotFoundException(string fieldName)
        : base($"Form field '{fieldName}' was not found in the document.") => FieldName = fieldName;

    public FieldNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

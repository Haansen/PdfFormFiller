namespace PdfFormFiller;

/// <summary>How form fields are written when the document is saved.</summary>
public enum OutputMode
{
    /// <summary>Fields stay as fields and can be edited by the recipient.</summary>
    Editable,

    /// <summary>Fields stay as fields but are flagged read-only. Values remain machine-readable.</summary>
    ReadOnly,

    /// <summary>Field appearances are drawn into the page content and all form fields are removed. Nothing is editable.</summary>
    Flattened,
}

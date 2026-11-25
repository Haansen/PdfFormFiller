using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>Document-level metadata of a generated document. Everything is optional.</summary>
internal sealed class PdfDocumentInfo
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    /// <summary>The application the report comes from.</summary>
    public string? Creator { get; set; }
    /// <summary>Document language as a BCP 47 tag, e.g. "sv-SE".</summary>
    public string? Language { get; set; }
    /// <summary>Creation date. Set it to get identical bytes for identical input; otherwise the time of saving is used.</summary>
    public DateTimeOffset? CreationDate { get; set; }
    /// <summary>Show the title instead of the file name in the reader's title bar. Only has an effect when there is a title.</summary>
    public bool DisplayTitle { get; set; } = true;
}

/// <summary>How an attached file relates to the document.</summary>
internal enum AttachmentRelationship { Unspecified, Source, Data, Alternative, Supplement }

/// <summary>A file attached to a generated document.</summary>
internal sealed class PdfAttachment
{
    /// <summary>File name without path, unique in the document.</summary>
    public required string FileName { get; init; }
    public required byte[] Data { get; init; }
    /// <summary>E.g. "text/csv".</summary>
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Description { get; init; }
    /// <summary>Modification date; the document's creation date when not given.</summary>
    public DateTimeOffset? Date { get; init; }
    public AttachmentRelationship Relationship { get; init; } = AttachmentRelationship.Unspecified;
}

/// <summary>An entry in the bookmark panel.</summary>
internal sealed class OutlineItem
{
    internal OutlineItem(string title, PdfPageBuilder page, double? top) { Title = title; Page = page; Top = top; }

    internal string Title { get; }
    internal PdfPageBuilder Page { get; }
    internal double? Top { get; }
    internal List<OutlineItem> Children { get; } = new();

    /// <summary>Whether the children are shown from the start.</summary>
    public bool Open { get; set; }

    /// <summary>Adds an entry below this one.</summary>
    /// <param name="title">Text in the panel.</param>
    /// <param name="page">Page to go to.</param>
    /// <param name="top">y coordinate to place at the top of the window; null for the top of the page.</param>
    public OutlineItem Add(string title, PdfPageBuilder page, double? top = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(page);
        var item = new OutlineItem(title, page, top);
        Children.Add(item);
        return item;
    }
}

/// <summary>One page of a generated document, see <see cref="PdfBuilder.AddPage"/>.</summary>
internal sealed class PdfPageBuilder
{
    private ContentWriter? _content;
    private readonly PdfArray _streams = new();
    private readonly PdfDictionary _fonts = new();
    private readonly PdfDictionary _images = new();

    internal PdfPageBuilder(PdfBuilder builder, int index, double width, double height)
    {
        Builder = builder;
        Index = index;
        Width = width;
        Height = height;
        Dictionary = builder.Document.AddPage(width, height);
        var resources = Dictionary.Dict("/Resources")!;
        resources["/Font"] = _fonts;
        resources["/XObject"] = _images;
        Dictionary["/Contents"] = _streams;
        if (builder.Tagged)
        {
            StructKey = builder.Structure.NextKey();
            Dictionary["/StructParents"] = new PdfNumber(StructKey);
            Dictionary["/Tabs"] = new PdfName("/S");
        }
    }

    internal PdfBuilder Builder { get; }
    internal PdfDictionary Dictionary { get; }
    internal int StructKey { get; }
    /// <summary>Structure element of each marked-content id on the page.</summary>
    internal List<int> ContentOwners { get; } = new();

    /// <summary>0-based page number.</summary>
    public int Index { get; }
    public double Width { get; }
    public double Height { get; }

    /// <summary>
    /// The page's content. After <see cref="Close"/> it gives a fresh writer whose content is drawn on top of the earlier one;
    /// that is how "page 1 of 7" is added when the number of pages is known.
    /// </summary>
    public ContentWriter Content => _content ??= new ContentWriter(this);

    /// <summary>
    /// Compresses what has been drawn and frees the buffer. Close each page when it is done to keep memory low;
    /// <see cref="PdfBuilder.Save()"/> closes the rest.
    /// </summary>
    public void Close()
    {
        if (_content is null) return;
        var bytes = _content.Finish();
        if (bytes.Length > 0)
        {
            var stream = new PdfStream(Array.Empty<byte>());
            stream.SetRawFlate(bytes);
            _streams.Add(Builder.Document.RefFor(stream));
        }
        _content = null;
    }

    internal void Use(PdfFont font) => _fonts[font.ResourceName] ??= Builder.Document.RefFor(font.Dictionary);
    internal void Use(PdfImage image) => _images[image.ResourceName] ??= Builder.Document.RefFor(image.Stream);

    /// <summary>
    /// Makes a rectangle (lower left corner, size) a link to a web address or e-mail address. Draw the link text separately.
    /// </summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Bottom edge.</param>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    /// <param name="uri">An absolute http, https or mailto address; everything else throws <see cref="PdfFormException"/>.</param>
    /// <param name="description">What assistive technology says about the link.</param>
    /// <param name="element">The <see cref="StructType.Link"/> element the link text is tagged with; needed for accessibility in tagged documents.</param>
    public void AddLink(double x, double y, double width, double height, string uri, string? description = null, StructId? element = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto") || uri.Any(char.IsControl))
            throw new PdfFormException("A link must be an absolute http, https or mailto address.");
        if (element is { } link && Builder.Tagged && Builder.Structure.TypeOf(link) != StructType.Link)
            throw new InvalidOperationException("The element of a link must be of type Link.");
        var target = parsed.AbsoluteUri;
        if (!Ascii(target) && parsed.Scheme != "mailto") target = new UriBuilder(parsed) { Host = parsed.IdnHost }.Uri.AbsoluteUri;
        if (!Ascii(target)) throw new PdfFormException("A link contains characters that cannot be encoded.");

        var action = new PdfDictionary();
        action["/S"] = new PdfName("/URI");
        action["/URI"] = new PdfString(Encoding.ASCII.GetBytes(target));
        var annot = new PdfDictionary();
        annot["/Type"] = new PdfName("/Annot");
        annot["/Subtype"] = new PdfName("/Link");
        annot["/Rect"] = new PdfArray(new[] { x, y, x + width, y + height }.Select(v => (PdfObject)new PdfNumber(Math.Round(Finite(v), 3))));
        annot["/Border"] = new PdfArray(new PdfNumber(0), new PdfNumber(0), new PdfNumber(0));
        annot["/F"] = new PdfNumber(4);
        annot["/A"] = action;
        if (description is not null) annot["/Contents"] = PdfString.FromText(description);
        if (Dictionary.Arr("/Annots") is not { } annots) Dictionary["/Annots"] = annots = new PdfArray();
        annots.Add(Builder.Document.RefFor(annot));
        if (element is { } e && Builder.Tagged) Builder.Structure.AddAnnotation(e, annot, this);

        static bool Ascii(string s) => s.All(c => c is > ' ' and < '\x7F');
        static double Finite(double v) => double.IsFinite(v) ? v : throw new ArgumentOutOfRangeException(nameof(v), "A coordinate or size is not a usable number.");
    }
}

/// <summary>
/// Generates a PDF: pages, fonts, images, tags, bookmarks, links and attachments. Typical use:
/// <code>
/// var pdf = new PdfBuilder(tagged: true) { Info = { Title = "Orders", Language = "sv-SE" } };
/// var font = pdf.AddFont(new TrueTypeFont(ttfBytes));
/// var page = pdf.AddPage(595.28, 841.89);
/// var h1 = pdf.Structure.Add(pdf.Structure.Document, StructType.H1);
/// page.Content.BeginTagged(h1);
/// page.Content.ShowText(font, 18, 50, 780, "Orders");
/// page.Content.EndMarked();
/// page.Close();
/// byte[] bytes = pdf.Save();
/// </code>
/// The output is PDF 1.7 with object streams and an xref stream. With <see cref="PdfDocumentInfo.CreationDate"/> set, the same calls give the same bytes
/// on the same .NET runtime. To go on with fields, merging or page operations, load the bytes as any other file.
/// Not thread-safe.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly Dictionary<string, PdfFont> _fonts = new();
    private readonly Dictionary<string, PdfImage> _images = new();
    private readonly List<PdfPageBuilder> _pages = new();
    private readonly List<OutlineItem> _outline = new();
    private readonly List<PdfAttachment> _attachments = new();

    /// <param name="tagged">Produce a tagged (accessible) document. When false, <see cref="Structure"/> and the tagging methods do nothing.</param>
    public PdfBuilder(bool tagged = true)
    {
        Tagged = tagged;
        Structure = new StructureBuilder(this);
    }

    public bool Tagged { get; }
    public PdfDocumentInfo Info { get; } = new();
    /// <summary>The tag tree.</summary>
    public StructureBuilder Structure { get; }
    public IReadOnlyList<PdfPageBuilder> Pages => _pages;
    internal PdfDocument Document { get; } = PdfDocument.CreateEmpty();

    /// <summary>Adds a page at the end. Size in points (1/72 inch), between 3 and 14,400; A4 is 595.28 x 841.89.</summary>
    public PdfPageBuilder AddPage(double width, double height)
    {
        var page = new PdfPageBuilder(this, _pages.Count, width, height);
        _pages.Add(page);
        return page;
    }

    /// <summary>The font for use in this document. The same font file gives the same object.</summary>
    public PdfFont AddFont(TrueTypeFont face)
    {
        ArgumentNullException.ThrowIfNull(face);
        var key = Convert.ToHexString(face.Hash);
        if (!_fonts.TryGetValue(key, out var font))
            _fonts[key] = font = new PdfFont(face, "/F" + (_fonts.Count + 1).ToString(CultureInfo.InvariantCulture), new PdfDictionary());
        return font;
    }

    /// <summary>Reads a JPEG or PNG, see <see cref="PdfImage.Load"/>. Identical files are stored once.</summary>
    public PdfImage AddImage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var key = Convert.ToHexString(SHA256.HashData(data));
        if (_images.TryGetValue(key, out var image)) return image;
        image = PdfImage.Load(data);
        image.ResourceName = "/Im" + (_images.Count + 1).ToString(CultureInfo.InvariantCulture);
        if (image.Mask is not null) image.Stream["/SMask"] = Document.RefFor(image.Mask);
        return _images[key] = image;
    }

    /// <summary>Adds a top-level bookmark; parameters as in <see cref="OutlineItem.Add"/>. Each costs about 1 KB of memory.</summary>
    public OutlineItem AddOutline(string title, PdfPageBuilder page, double? top = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(page);
        var item = new OutlineItem(title, page, top);
        _outline.Add(item);
        return item;
    }

    /// <summary>Attaches a file. Throws <see cref="PdfFormException"/> for a name that is empty, a path, or already used.</summary>
    public void Attach(PdfAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var name = attachment.FileName;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c)))
            throw new PdfFormException("An attachment needs a plain file name without path.");
        if (_attachments.Any(a => string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase)))
            throw new PdfFormException($"There is already an attachment named '{name}'.");
        var mime = attachment.MimeType.Split('/');
        if (mime.Length != 2 || mime.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$&-^_.+".Contains(c))))
            throw new PdfFormException($"'{attachment.MimeType}' is not a MIME type.");
        _attachments.Add(attachment);
    }

    /// <summary>The finished document. May be called several times, also after adding more.</summary>
    public byte[] Save()
    {
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    /// <summary>Writes the finished document without holding it in memory. The stream need not be seekable.</summary>
    /// <exception cref="PdfFormException">There are no pages.</exception>
    /// <exception cref="InvalidOperationException">Saved state or marked content is left open on a page.</exception>
    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_pages.Count == 0) throw new PdfFormException("The document has no pages.");
        foreach (var page in _pages) page.Close();
        foreach (var font in _fonts.Values) font.Finish(Document);
        WriteMetadata(Info.CreationDate ?? DateTimeOffset.Now);
        WriteOutline();
        WriteAttachments(Info.CreationDate ?? DateTimeOffset.Now);
        if (Tagged)
        {
            var mark = new PdfDictionary();
            mark["/Marked"] = PdfBool.True;
            Document.Catalog["/MarkInfo"] = mark;
        }
        CompactWriter.Write(Document, Tagged ? Structure : null, destination);
    }

    // ------------------------------------------------------------------ metadata

    private static string Date(DateTimeOffset d)
    {
        var offset = d.Offset;
        return d.ToString("'D:'yyyyMMddHHmmss", CultureInfo.InvariantCulture)
               + (offset == TimeSpan.Zero ? "Z" : $"{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh}'{offset.Duration():mm}'");
    }

    private static PdfString DateString(DateTimeOffset d) => new(Encoding.ASCII.GetBytes(Date(d)));

    private void WriteMetadata(DateTimeOffset now)
    {
        const string producer = "PdfFormFiller";
        var info = Document.Trailer.Dict("/Info") ?? new PdfDictionary();
        void Set(string key, string? value) => info[key] = string.IsNullOrEmpty(value) ? null : PdfString.FromText(value);
        Set("/Title", Info.Title);
        Set("/Author", Info.Author);
        Set("/Subject", Info.Subject);
        Set("/Keywords", Info.Keywords);
        Set("/Creator", Info.Creator);
        Set("/Producer", producer);
        info["/CreationDate"] = DateString(now);
        info["/ModDate"] = DateString(now);
        Document.Trailer["/Info"] = Document.RefFor(info);

        var catalog = Document.Catalog;
        catalog["/Lang"] = string.IsNullOrEmpty(Info.Language) ? null : PdfString.FromText(Info.Language);
        catalog["/ViewerPreferences"] = null;
        if (Info.DisplayTitle && !string.IsNullOrEmpty(Info.Title))
        {
            var prefs = new PdfDictionary();
            prefs["/DisplayDocTitle"] = PdfBool.True;
            catalog["/ViewerPreferences"] = prefs;
        }

        static string X(string s) => SecurityElement.Escape(new string(s.Where(c => c is '\t' or '\n' or '\r' || c >= ' ' && c is not ('￾' or '￿')).ToArray()));
        var iso = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (now.Offset == TimeSpan.Zero ? "Z" : now.ToString("zzz", CultureInfo.InvariantCulture));
        var sb = new StringBuilder("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        sb.Append("<dc:format>application/pdf</dc:format>\n");
        if (!string.IsNullOrEmpty(Info.Title)) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(Info.Title)}</rdf:li></rdf:Alt></dc:title>\n");
        if (!string.IsNullOrEmpty(Info.Author)) sb.Append($"<dc:creator><rdf:Seq><rdf:li>{X(Info.Author)}</rdf:li></rdf:Seq></dc:creator>\n");
        if (!string.IsNullOrEmpty(Info.Subject)) sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(Info.Subject)}</rdf:li></rdf:Alt></dc:description>\n");
        if (!string.IsNullOrEmpty(Info.Language)) sb.Append($"<dc:language><rdf:Bag><rdf:li>{X(Info.Language)}</rdf:li></rdf:Bag></dc:language>\n");
        if (!string.IsNullOrEmpty(Info.Keywords)) sb.Append($"<pdf:Keywords>{X(Info.Keywords)}</pdf:Keywords>\n");
        sb.Append($"<pdf:Producer>{producer}</pdf:Producer>\n");
        if (!string.IsNullOrEmpty(Info.Creator)) sb.Append($"<xmp:CreatorTool>{X(Info.Creator)}</xmp:CreatorTool>\n");
        sb.Append($"<xmp:CreateDate>{iso}</xmp:CreateDate>\n<xmp:ModifyDate>{iso}</xmp:ModifyDate>\n</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        var metadata = new PdfStream(Encoding.UTF8.GetBytes(sb.ToString()));
        metadata["/Type"] = new PdfName("/Metadata");
        metadata["/Subtype"] = new PdfName("/XML");
        catalog["/Metadata"] = Document.RefFor(metadata);
    }

    // ------------------------------------------------------------------ outline

    private void WriteOutline()
    {
        Document.Catalog["/Outlines"] = null;
        if (_outline.Count == 0) return;
        var root = new PdfDictionary();
        root["/Type"] = new PdfName("/Outlines");
        Document.Catalog["/Outlines"] = Document.RefFor(root);
        Document.Catalog["/PageMode"] = new PdfName("/UseOutlines");

        // parents before children; the visible counts are added up in reverse afterwards
        var all = new List<(OutlineItem? Item, PdfDictionary Dict, int Parent)> { (null, root, -1) };
        for (var i = 0; i < all.Count; i++)
        {
            var (parent, parentDict, _) = all[i];
            var children = parent?.Children ?? _outline;
            PdfDictionary? previous = null;
            foreach (var child in children)
            {
                var d = new PdfDictionary();
                d["/Title"] = PdfString.FromText(child.Title);
                d["/Parent"] = Document.RefFor(parentDict);
                d["/Dest"] = new PdfArray(Document.RefFor(child.Page.Dictionary), new PdfName("/XYZ"), PdfNull.Instance,
                    new PdfNumber(Math.Round(child.Top ?? child.Page.Height, 3)), PdfNull.Instance);
                if (previous is null) parentDict["/First"] = Document.RefFor(d);
                else
                {
                    previous["/Next"] = Document.RefFor(d);
                    d["/Prev"] = Document.RefFor(previous);
                }
                parentDict["/Last"] = Document.RefFor(d);
                previous = d;
                all.Add((child, d, i));
            }
        }
        var visible = new int[all.Count];
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var open = all[i].Item?.Open ?? true;
            if (visible[i] > 0) all[i].Dict["/Count"] = new PdfNumber(open ? visible[i] : -visible[i]);
            if (i > 0) visible[all[i].Parent] += 1 + (open ? visible[i] : 0);
        }
    }

    // ------------------------------------------------------------------ attachments

    private void WriteAttachments(DateTimeOffset now)
    {
        if (_attachments.Count == 0) return;
        var entries = new List<(byte[] Key, PdfString Name, PdfReference Spec)>();
        var af = new PdfArray();
        foreach (var a in _attachments)
        {
            var file = new PdfStream(Array.Empty<byte>());
            file.SetData(a.Data);
            file["/Type"] = new PdfName("/EmbeddedFile");
            file["/Subtype"] = new PdfName("/" + a.MimeType);
            var p = new PdfDictionary();
            p["/Size"] = new PdfNumber(a.Data.Length);
            p["/CreationDate"] = DateString(a.Date ?? now);
            p["/ModDate"] = DateString(a.Date ?? now);
            p["/CheckSum"] = new PdfString(MD5.HashData(a.Data), hex: true);
            file["/Params"] = p;
            var ef = new PdfDictionary();
            ef["/F"] = Document.RefFor(file);
            ef["/UF"] = Document.RefFor(file);
            var spec = new PdfDictionary();
            spec["/Type"] = new PdfName("/Filespec");
            spec["/F"] = PdfString.FromText(a.FileName);
            spec["/UF"] = PdfString.FromText(a.FileName);
            if (!string.IsNullOrEmpty(a.Description)) spec["/Desc"] = PdfString.FromText(a.Description);
            spec["/AFRelationship"] = new PdfName("/" + a.Relationship);
            spec["/EF"] = ef;
            var r = Document.RefFor(spec);
            af.Add(r);
            var name = PdfString.FromText(a.FileName);
            entries.Add((name.Bytes, name, r));
        }
        entries.Sort((x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key));
        var names = new PdfArray();
        foreach (var e in entries) { names.Add(e.Name); names.Add(e.Spec); }
        var tree = new PdfDictionary();
        tree["/Names"] = names;
        var all = new PdfDictionary();
        all["/EmbeddedFiles"] = Document.RefFor(tree);
        Document.Catalog["/Names"] = all;
        Document.Catalog["/AF"] = af;
    }
}

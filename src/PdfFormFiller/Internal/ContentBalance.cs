namespace PdfFormFiller.Internal;

/// <summary>What a page content stream leaves behind: unclosed <c>q</c> levels, extra <c>Q</c>, and the matrix in force.</summary>
internal sealed record ContentBalance(int OpenStates, bool Underflowed, bool OpenText, double[] BaseMatrix)
{
    private const int MaxOpenStates = 1000;
    private static readonly double[] Identity = { 1, 0, 0, 1, 0, 0 };

    /// <summary>
    /// Scans operators (q, Q, cm, BT, ET; strings, comments and inline images are skipped over) to find out whether the
    /// stream is balanced. Returns null when the stream cannot be followed reliably, in which case callers assume it is.
    /// </summary>
    public static ContentBalance? Analyze(byte[] data)
    {
        var stack = new List<double[]>();
        var cur = Identity;
        var underflow = false;
        var inText = false;
        var operands = new List<double>();
        var i = 0;
        var n = data.Length;
        while (i < n)
        {
            var c = data[i];
            if (IsWhite(c)) { i++; continue; }
            switch (c)
            {
                case (byte)'%':
                    while (i < n && data[i] != '\n' && data[i] != '\r') i++;
                    continue;
                case (byte)'(':
                    if (!SkipString(data, ref i)) return null;
                    operands.Add(double.NaN);
                    continue;
                case (byte)'<':
                    if (i + 1 < n && data[i + 1] == '<') { i += 2; continue; }
                    while (i < n && data[i] != '>') i++;
                    i++;
                    operands.Add(double.NaN);
                    continue;
                case (byte)'/':
                    i++;
                    while (i < n && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
                    operands.Add(double.NaN);
                    continue;
                case (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)')':
                    i++;
                    continue;
            }

            var start = i;
            while (i < n && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
            var token = System.Text.Encoding.Latin1.GetString(data, start, i - start);
            if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                operands.Add(double.IsFinite(number) ? number : double.NaN);
                continue;
            }
            switch (token)
            {
                case "q":
                    if (stack.Count >= MaxOpenStates) return null;
                    stack.Add(cur);
                    break;
                case "Q":
                    if (stack.Count > 0) { cur = stack[^1]; stack.RemoveAt(stack.Count - 1); }
                    else { underflow = true; cur = Identity; }
                    break;
                case "cm":
                    if (operands.Count < 6) return null;
                    var m = operands.GetRange(operands.Count - 6, 6).ToArray();
                    if (m.Any(double.IsNaN)) return null;
                    cur = Multiply(m, cur);
                    break;
                case "BT": inText = true; break;
                case "ET": inText = false; break;
                case "ID":
                    if (!SkipInlineImage(data, ref i)) return null;
                    break;
            }
            operands.Clear();
        }
        return new ContentBalance(stack.Count, underflow, inText, stack.Count > 0 ? stack[0] : cur);
    }

    /// <summary>Operators that return the graphics state to the one before the stream, as far as it can be derived.</summary>
    public string Epilogue()
    {
        var sb = new System.Text.StringBuilder();
        if (OpenText) sb.Append("ET\n");
        for (var k = 0; k < OpenStates; k++) sb.Append("Q\n");
        if (!Underflowed)
        {
            sb.Append("Q\n"); // the wrapper's own q
            return sb.ToString();
        }
        // The stream popped the wrapper's q; undo whatever it applied to the matrix afterwards.
        var inv = Invert(BaseMatrix);
        if (inv is not null && !IsIdentity(BaseMatrix))
            sb.Append(string.Join(' ', inv.Select(PdfExt.Fmt))).Append(" cm\n");
        return sb.ToString();
    }

    private static bool IsIdentity(double[] m) => m.Zip(Identity).All(p => Math.Abs(p.First - p.Second) < 1e-9);

    private static double[] Multiply(double[] m, double[] c) => new[]
    {
        m[0] * c[0] + m[1] * c[2],
        m[0] * c[1] + m[1] * c[3],
        m[2] * c[0] + m[3] * c[2],
        m[2] * c[1] + m[3] * c[3],
        m[4] * c[0] + m[5] * c[2] + c[4],
        m[4] * c[1] + m[5] * c[3] + c[5],
    };

    private static double[]? Invert(double[] m)
    {
        var det = m[0] * m[3] - m[1] * m[2];
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12) return null;
        var inv = new[]
        {
            m[3] / det, -m[1] / det, -m[2] / det, m[0] / det,
            (m[2] * m[5] - m[3] * m[4]) / det,
            (m[1] * m[4] - m[0] * m[5]) / det,
        };
        return inv.All(double.IsFinite) ? inv : null;
    }

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
        or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool SkipString(byte[] d, ref int i)
    {
        var depth = 0;
        while (i < d.Length)
        {
            var b = d[i++];
            if (b == '\\') { i++; continue; }
            if (b == '(') depth++;
            else if (b == ')' && --depth == 0) return true;
        }
        return false;
    }

    /// <summary>Called right after the ID token: skips the binary data up to and including the EI token.</summary>
    private static bool SkipInlineImage(byte[] d, ref int i)
    {
        if (i < d.Length) i++; // single whitespace after ID
        for (; i + 1 < d.Length; i++)
        {
            if (d[i] != 'E' || d[i + 1] != 'I') continue;
            var before = i == 0 || IsWhite(d[i - 1]);
            var after = i + 2 >= d.Length || IsWhite(d[i + 2]) || IsDelimiter(d[i + 2]);
            if (before && after) { i += 2; return true; }
        }
        return false;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>
/// Standard security handler (ISO 32000-1 §7.6.3, ISO 32000-2 for R6). Decrypts documents that open without a
/// user password (the common "owner password only" protection) or with a given user or owner password. Output is always
/// written unencrypted.
/// </summary>
internal sealed class Decryptor
{
    private static readonly byte[] Pad =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private enum Method { None, Rc4, AesV2, AesV3 }

    private readonly byte[] _key;
    private readonly Method _streams;
    private readonly Method _strings;
    private readonly bool _encryptMetadata;

    private Decryptor(byte[] key, Method streams, Method strings, bool encryptMetadata)
    {
        _key = key; _streams = streams; _strings = strings; _encryptMetadata = encryptMetadata;
    }

    /// <summary>
    /// Derives the file key. The empty user password is always tried first; then <paramref name="password"/>, as user and as
    /// owner password. The password itself is not kept.
    /// </summary>
    public static Decryptor Create(PdfDictionary enc, byte[] firstId, string? password = null)
    {
        var filter = enc.Name("/Filter");
        if (filter != "/Standard") throw new PdfFormException($"Unsupported encryption handler {filter}.");
        var v = enc.Int("/V") ?? 0;
        var r = enc.Int("/R") ?? 2;
        if (r is < 2 or > 6 || v > 5) throw new PdfFormException($"Unsupported encryption version (V {v}, R {r}).");
        var o = enc.StrObj("/O")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /O.");
        var u = enc.StrObj("/U")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /U.");
        var p = unchecked((int)(uint)(enc.Long("/P") ?? -1)); // some writers store /P as an unsigned 32-bit value
        var encryptMetadata = v < 4 || (enc.Bool("/EncryptMetadata") ?? true);

        var stmf = Method.Rc4; var strf = Method.Rc4;
        int? filterLength = null;
        if (v >= 4)
        {
            stmf = CryptFilterMethod(enc, enc.Name("/StmF") ?? "/Identity", ref filterLength);
            strf = CryptFilterMethod(enc, enc.Name("/StrF") ?? "/Identity", ref filterLength);
        }

        byte[]? key;
        if (r >= 5)
        {
            var ue = enc.StrObj("/UE")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /UE.");
            key = UserKeyR6(r, Array.Empty<byte>(), u, ue);
            if (key is null && !string.IsNullOrEmpty(password))
            {
                var oe = enc.StrObj("/OE")?.Bytes;
                foreach (var pw in PasswordBytesR6(password))
                {
                    key = UserKeyR6(r, pw, u, ue) ?? OwnerKeyR6(r, pw, o, oe, u);
                    if (key is not null) break;
                }
            }
        }
        else
        {
            var lengths = KeyLengths(v, r, enc.Int("/Length"), filterLength, stmf == Method.AesV2 || strf == Method.AesV2);
            key = UserKeyLegacy(r, lengths, Pad, o, u, p, firstId, encryptMetadata);
            if (key is null && !string.IsNullOrEmpty(password) && PdfDocEncoding.TryEncode(password, out var pw))
            {
                var padded = PadPassword(pw);
                key = UserKeyLegacy(r, lengths, padded, o, u, p, firstId, encryptMetadata)
                      ?? OwnerKeyLegacy(r, lengths, padded, o, u, p, firstId, encryptMetadata);
            }
        }
        if (key is null)
            throw new PdfFormException(string.IsNullOrEmpty(password)
                ? "The PDF requires a password to open."
                : "The supplied password is incorrect for this PDF.");
        return new Decryptor(key, stmf, strf, encryptMetadata);
    }

    private static Method CryptFilterMethod(PdfDictionary enc, string name, ref int? length)
    {
        if (name == "/Identity") return Method.None;
        var cf = enc.Dict("/CF")?.Dict(name) ?? throw new PdfFormException($"Encrypt dictionary lacks the crypt filter {name}.");
        length ??= cf.Int("/Length");
        return cf.Name("/CFM") switch
        {
            "/AESV2" => Method.AesV2,
            "/AESV3" => Method.AesV3,
            "/V2" => Method.Rc4,
            "/None" or null => Method.None,
            var other => throw new PdfFormException($"Unsupported crypt filter method {other}."),
        };
    }

    // ------------------------------------------------------------------ passwords

    /// <summary>First 32 bytes of the password, filled up with the standard padding.</summary>
    private static byte[] PadPassword(byte[] password)
    {
        var padded = new byte[32];
        var n = Math.Min(32, password.Length);
        Array.Copy(password, padded, n);
        Array.Copy(Pad, 0, padded, n, 32 - n);
        return padded;
    }

    /// <summary>
    /// UTF-8 forms of an R5/R6 password, at most 127 bytes each: after the SASLprep mappings and NFKC (RFC 4013 §2.1 and
    /// §2.2; nothing is prohibited, since the point is to open files), then verbatim for writers that skip the preparation.
    /// </summary>
    private static List<byte[]> PasswordBytesR6(string password)
    {
        var list = new List<byte[]>();
        void Add(Func<string> form)
        {
            byte[] bytes;
            try { bytes = new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(form()); }
            catch (ArgumentException) { return; } // unpaired surrogates: no valid encoding
            if (bytes.Length > 127) bytes = bytes.AsSpan(0, 127).ToArray();
            if (bytes.Length > 0 && !list.Any(b => b.AsSpan().SequenceEqual(bytes))) list.Add(bytes);
        }
        Add(() =>
        {
            var sb = new StringBuilder(password.Length);
            foreach (var c in password)
            {
                // mapped to nothing (RFC 3454 B.1)
                if (c is '\u00AD' or '\u034F' or '\u1806' or (>= '\u180B' and <= '\u180D') or (>= '\u200B' and <= '\u200D')
                    or '\u2060' or (>= '\uFE00' and <= '\uFE0F') or '\uFEFF') continue;
                // non-ASCII spaces (RFC 3454 C.1.2)
                sb.Append(c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000' ? ' ' : c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormKC);
        });
        Add(() => password);
        return list;
    }

    // ------------------------------------------------------------------ key computation

    /// <summary>
    /// Key lengths in bytes to try for R2-R4, most likely first. /Length is unreliable in practice (missing, or in bytes
    /// instead of bits), and the /U check tells a wrong guess apart, so the two usual lengths always get a chance.
    /// </summary>
    private static List<int> KeyLengths(int v, int r, int? dictLength, int? filterLength, bool aes)
    {
        if (r == 2) return new List<int> { 5 };
        var declared = v switch
        {
            1 => 5,
            >= 4 => aes ? 16 : KeyBytes(filterLength) ?? KeyBytes(dictLength) ?? 16,
            _ => KeyBytes(dictLength) ?? 5,
        };
        return new[] { declared, 16, 5, KeyBytes(filterLength) ?? 16, KeyBytes(dictLength) ?? 16 }.Distinct().ToList();
    }

    private static int? KeyBytes(int? length) => length switch
    {
        >= 5 and <= 16 => length,          // written in bytes
        >= 40 => Math.Min(length.Value / 8, 16),
        _ => null,
    };

    private static byte[]? UserKeyLegacy(int r, List<int> lengths, byte[] padded, byte[] o, byte[] u, int p, byte[] id, bool encryptMetadata)
    {
        foreach (var n in lengths)
        {
            var key = ComputeKeyLegacy(r, n, padded, o, p, id, encryptMetadata);
            if (ValidateUserLegacy(r, key, u, id)) return key;
        }
        return null;
    }

    /// <summary>Algorithm 7: the owner password decrypts /O, which gives the padded user password.</summary>
    private static byte[]? OwnerKeyLegacy(int r, List<int> lengths, byte[] padded, byte[] o, byte[] u, int p, byte[] id, bool encryptMetadata)
    {
        if (o.Length < 32) return null;
        var o32 = o.AsSpan(0, 32).ToArray();
        foreach (var n in lengths)
        {
            // writers disagree on whether the 50 extra rounds hash the whole digest or only the key-length part of it
            foreach (var part in r >= 3 && n < 16 ? new[] { 16, n } : new[] { 16 })
            {
                var hash = MD5.HashData(padded);
                if (r >= 3)
                    for (var i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, part));
                var key = hash.AsSpan(0, n).ToArray();
                var user = o32;
                if (r == 2) user = Rc4(key, user);
                else
                    for (var i = 19; i >= 0; i--) user = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), user);
                if (UserKeyLegacy(r, new List<int> { n }, user, o, u, p, id, encryptMetadata) is { } fileKey) return fileKey;
            }
        }
        return null;
    }

    private static byte[] ComputeKeyLegacy(int r, int n, byte[] paddedPassword, byte[] o, int p, byte[] id, bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        var ms = new MemoryStream();
        ms.Write(paddedPassword, 0, 32);
        ms.Write(o, 0, Math.Min(32, o.Length));
        ms.Write(BitConverter.GetBytes(p), 0, 4);  // little-endian signed
        ms.Write(id, 0, id.Length);
        if (r >= 4 && !encryptMetadata) ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4);
        var hash = md5.ComputeHash(ms.ToArray());
        if (r >= 3)
            for (var i = 0; i < 50; i++) hash = md5.ComputeHash(hash, 0, n);
        var key = new byte[n];
        Array.Copy(hash, key, n);
        return key;
    }

    private static bool ValidateUserLegacy(int r, byte[] key, byte[] u, byte[] id)
    {
        if (r == 2)
        {
            var x = Rc4(key, Pad);
            return u.Length >= 32 && x.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32));
        }
        using var md5 = MD5.Create();
        var ms = new MemoryStream();
        ms.Write(Pad, 0, 32); ms.Write(id, 0, id.Length);
        var h = md5.ComputeHash(ms.ToArray());
        var x2 = Rc4(key, h);
        for (var i = 1; i <= 19; i++)
        {
            var k = new byte[key.Length];
            for (var j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ i);
            x2 = Rc4(k, x2);
        }
        return u.Length >= 16 && x2.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>File key from a user password (algorithm 2.A), or null when the password does not match /U.</summary>
    private static byte[]? UserKeyR6(int r, byte[] pw, byte[] u, byte[] ue)
        => u.Length < 48 ? null : KeyR6(r, pw, u, ue, Array.Empty<byte>(), "/UE");

    /// <summary>File key from an owner password, or null when the password does not match /O.</summary>
    private static byte[]? OwnerKeyR6(int r, byte[] pw, byte[] o, byte[]? oe, byte[] u)
        => o.Length < 48 || u.Length < 48 ? null : KeyR6(r, pw, o, oe, u.AsSpan(0, 48).ToArray(), "/OE");

    private static byte[]? KeyR6(int r, byte[] pw, byte[] check, byte[]? wrapped, byte[] udata, string wrappedName)
    {
        var hash = HashR6(r, pw, check.AsSpan(32, 8).ToArray(), udata);
        if (!hash.AsSpan(0, 32).SequenceEqual(check.AsSpan(0, 32))) return null;
        if (wrapped is null || wrapped.Length < 32) throw new PdfFormException($"Encrypt dictionary has a damaged {wrappedName}.");
        using var aes = Aes.Create();
        aes.Key = HashR6(r, pw, check.AsSpan(40, 8).ToArray(), udata);
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(wrapped, 0, 32);
    }

    /// <summary>Algorithm 2.B (R6) or plain SHA-256 (R5).</summary>
    private static byte[] HashR6(int r, byte[] pw, byte[] salt, byte[] udata)
    {
        var input = Concat(pw, salt, udata);
        var k = SHA256.HashData(input);
        if (r == 5) return k;
        var i = 0;
        while (true)
        {
            var k1Unit = Concat(pw, k, udata);
            var k1 = new byte[k1Unit.Length * 64];
            for (var j = 0; j < 64; j++) Buffer.BlockCopy(k1Unit, 0, k1, j * k1Unit.Length, k1Unit.Length);
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            aes.Key = k.AsSpan(0, 16).ToArray(); aes.IV = k.AsSpan(16, 16).ToArray();
            using var enc = aes.CreateEncryptor();
            var e = enc.TransformFinalBlock(k1, 0, k1.Length);
            var sum = 0;
            for (var j = 0; j < 16; j++) sum += e[j];
            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            i++;
            if (i >= 64 && e[^1] <= i - 32) break;
        }
        return k.AsSpan(0, 32).ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var r = new byte[len];
        var o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    // ------------------------------------------------------------------ decryption of objects

    public void DecryptObject(PdfObject obj, int num, int gen)
    {
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        Walk(obj, num, gen, seen);
    }

    private void Walk(PdfObject obj, int num, int gen, HashSet<PdfObject> seen)
    {
        if (!seen.Add(obj)) return;
        switch (obj)
        {
            case PdfString s:
                if (_strings != Method.None) s.Bytes = Decrypt(_strings, s.Bytes, num, gen);
                break;
            case PdfArray a:
                foreach (var it in a.Items) Walk(it, num, gen, seen);
                break;
            case PdfStream st:
            {
                var type = st.Name("/Type");
                if (type == "/XRef") break;
                foreach (var kv in st.Entries.ToList()) Walk(kv.Value, num, gen, seen);
                if (type == "/Metadata" && !_encryptMetadata) break;
                var method = _streams;
                var filters = Filters.NamesOf(st["/Filter"]);
                if (filters.Contains("/Crypt"))
                {
                    // explicit crypt filter on the stream: Identity unless named otherwise (rare); treat as identity
                    method = Method.None;
                }
                if (method != Method.None) st.SetRaw(Decrypt(method, st.Raw, num, gen));
                break;
            }
            case PdfDictionary d:
                foreach (var kv in d.Entries.ToList())
                    if (kv.Key != "/Contents" || !d.ContainsKey("/ByteRange")) // a signature's /Contents is never encrypted
                        Walk(kv.Value, num, gen, seen);
                break;
        }
    }

    private byte[] Decrypt(Method m, byte[] data, int num, int gen)
    {
        switch (m)
        {
            case Method.Rc4: return Rc4(ObjectKey(num, gen, false), data);
            case Method.AesV2: return AesCbc(ObjectKey(num, gen, true), data);
            case Method.AesV3: return AesCbc(_key, data);
            default: return data;
        }
    }

    private byte[] ObjectKey(int num, int gen, bool aes)
    {
        var ms = new MemoryStream();
        ms.Write(_key, 0, _key.Length);
        ms.Write(new[] { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8) }, 0, 5);
        if (aes) ms.Write(new byte[] { 0x73, 0x41, 0x6C, 0x54 }, 0, 4);
        var h = MD5.HashData(ms.ToArray());
        var n = Math.Min(_key.Length + 5, 16);
        return h.AsSpan(0, n).ToArray();
    }

    private static byte[] AesCbc(byte[] key, byte[] data)
    {
        if (data.Length < 16) return Array.Empty<byte>();
        try
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            aes.IV = data.AsSpan(0, 16).ToArray();
            var body = data.Length - 16;
            body -= body % 16;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(data, 16, body);
            // strip PKCS#5 padding
            if (plain.Length > 0)
            {
                int pad = plain[^1];
                if (pad >= 1 && pad <= 16 && pad <= plain.Length) Array.Resize(ref plain, plain.Length - pad);
            }
            return plain;
        }
        catch (CryptographicException)
        {
            return Array.Empty<byte>();
        }
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        int x = 0, y = 0;
        for (var k = 0; k < data.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
        }
        return output;
    }
}

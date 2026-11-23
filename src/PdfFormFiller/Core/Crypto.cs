using System.Security.Cryptography;
using System.Text;

namespace PdfFormFiller.Core;

/// <summary>
/// Standard security handler (ISO 32000-1 §7.6.3, ISO 32000-2 for R6). Decrypts documents that open without a
/// user password (the common "owner password only" protection). Output is always written unencrypted.
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

    public static Decryptor Create(PdfDictionary enc, byte[] firstId)
    {
        var filter = enc.Name("/Filter");
        if (filter != "/Standard") throw new PdfFormException($"Unsupported encryption handler {filter}.");
        var v = enc.Int("/V") ?? 0;
        var r = enc.Int("/R") ?? 2;
        var lengthBits = enc.Int("/Length") ?? 40;
        var o = enc.StrObj("/O")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /O.");
        var u = enc.StrObj("/U")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /U.");
        var p = unchecked((int)(uint)(enc.Long("/P") ?? -1)); // some writers store /P as an unsigned 32-bit value
        var encryptMetadata = enc.Bool("/EncryptMetadata") ?? true;

        var stmf = Method.Rc4; var strf = Method.Rc4;
        if (v >= 4)
        {
            stmf = CryptFilterMethod(enc, enc.Name("/StmF") ?? "/Identity", ref lengthBits);
            strf = CryptFilterMethod(enc, enc.Name("/StrF") ?? "/Identity", ref lengthBits);
        }

        byte[] key;
        if (r >= 5)
        {
            key = ComputeKeyR6(r, u, enc.StrObj("/UE")?.Bytes ?? throw new PdfFormException("Encrypt dictionary lacks /UE."))
                  ?? throw new PdfFormException("The PDF requires a password to open.");
        }
        else
        {
            var n = r == 2 ? 5 : Math.Clamp(lengthBits / 8, 5, 16);
            key = ComputeKeyLegacy(r, n, o, p, firstId, encryptMetadata);
            if (!ValidateUserLegacy(r, key, u, firstId))
                throw new PdfFormException("The PDF requires a password to open.");
        }
        return new Decryptor(key, stmf, strf, encryptMetadata);
    }

    private static Method CryptFilterMethod(PdfDictionary enc, string name, ref int lengthBits)
    {
        if (name == "/Identity") return Method.None;
        var cf = enc.Dict("/CF")?.Dict(name);
        if (cf is null) return Method.None;
        var len = cf.Int("/Length");
        if (len is not null) lengthBits = len.Value <= 40 ? len.Value * 8 : len.Value;
        return cf.Name("/CFM") switch
        {
            "/AESV2" => Method.AesV2,
            "/AESV3" => Method.AesV3,
            "/V2" => Method.Rc4,
            "/None" => Method.None,
            _ => Method.None,
        };
    }

    // ------------------------------------------------------------------ key computation

    private static byte[] ComputeKeyLegacy(int r, int n, byte[] o, int p, byte[] id, bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        var ms = new MemoryStream();
        ms.Write(Pad, 0, 32);                      // empty user password, padded
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

    private static byte[]? ComputeKeyR6(int r, byte[] u, byte[] ue)
    {
        if (u.Length < 48) return null;
        var validationSalt = u.AsSpan(32, 8).ToArray();
        var keySalt = u.AsSpan(40, 8).ToArray();
        var pw = Array.Empty<byte>();
        var hash = HashR6(r, pw, validationSalt, Array.Empty<byte>());
        if (!hash.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32))) return null;
        var intermediate = HashR6(r, pw, keySalt, Array.Empty<byte>());
        using var aes = Aes.Create();
        aes.Key = intermediate; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(ue, 0, 32);
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
                foreach (var kv in st.Entries.ToList()) Walk(kv.Value, num, gen, seen);
                var type = st.Name("/Type");
                if (type == "/XRef") break;
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
                foreach (var kv in d.Entries.ToList()) Walk(kv.Value, num, gen, seen);
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

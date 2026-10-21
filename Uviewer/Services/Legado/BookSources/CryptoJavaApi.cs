using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Array;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 4-5b：T0 加解密原语（<c>java</c> 的对称加密 / HMAC / 摘要 / 字节数组族）。
///
/// <para><b>语义来源</b>：legado <c>JsEncodeUtils.kt</c>（参数序、API 名）+
/// reader <c>js.rs</c> 的 <c>symmetric_crypt/symmetric_codec</c>（转换串解析、空 IV 补零、
/// 宽松 Base64、字节序列与文本 key 的区分）。已知差异：</para>
/// <list type="bullet">
/// <item>legado 的 <c>aesEncodeToString</c> 源码实际调了 <c>decryptStr</c>（复制粘贴错误）；
///   reader 修正为 <c>encryptHex</c>，这里跟随 reader（样本 0 调用，不影响任何现役书源）。</item>
/// <item>reader 用 latin1 字符串承载字节序列 + flags 位标记；这里直接用 <b>JS 数组（数字 0-255）</b>
///   表示字节序列（<c>base64DecodeToByteArray</c> 等的返回形态），字符串一律按 UTF-8 文本——
///   两者在 C# 侧天然可区分，不需要标记位。样本里的真实用法只有
///   <c>createSymmetricCrypto(transformation, base64DecodeToByteArray(...), base64DecodeToByteArray(...))</c> 这一条链。</item>
/// </list>
///
/// <para><b>失败语义</b>：解密失败 / 密钥长度非法 / 算法不支持 → 抛异常，由
/// <see cref="JsContextObjects.CreateFunction"/> 统一记 <see cref="JsDiagnosticKind.ScriptError"/> 并返回 null
/// （legado 返回 null 的 String?，语义一致）；脚本不中断。</para>
/// </summary>
internal static class CryptoJavaApi
{
    // ============================================================
    // java 原语入口（JsBridge.InvokeImplemented 派发；args 已过 ToObject）
    // ============================================================

    /// <summary><c>base64DecodeToByteArray(str[, flags])</c> → 字节数组（JS 数组）；空串 → null（legado 同）。</summary>
    public static JsValue Base64DecodeToByteArray(Engine engine, object?[] args)
    {
        var str = JsPureApi.AsString(args, 0);
        if (string.IsNullOrWhiteSpace(str))
        {
            return JsValue.Null;
        }

        return ToJsByteArray(engine, Base64DecodeLenient(str));
    }

    /// <summary><c>hexDecodeToByteArray(hex)</c> → 字节数组；空/非法 → null。</summary>
    public static JsValue HexDecodeToByteArray(Engine engine, object?[] args)
    {
        var hex = JsPureApi.AsString(args, 0);
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0 || !IsHex(hex))
        {
            return JsValue.Null;
        }

        return ToJsByteArray(engine, Convert.FromHexString(hex));
    }

    /// <summary>
    /// <c>createSymmetricCrypto(transformation, key[, iv])</c> → 对称加解密对象：
    /// <c>encrypt(data)/encryptBase64(data)/encryptHex(data)/decrypt(data)/decryptStr(data)/setIv(iv)</c>。
    /// key/iv 可为文本（UTF-8）或字节数组（<c>base64DecodeToByteArray</c> 的返回值）。
    /// <para><paramref name="sink"/> 用于对象方法的失败诊断（CreateFunction 只包住**注册时**的调用，
    /// 返回对象上的方法在脚本里另行调用，必须自己带 sink，否则加解密失败静默变 null）。</para>
    /// </summary>
    public static object? CreateSymmetricCrypto(Engine engine, object?[] args, JsContextObjects.JsErrorSink sink)
    {
        var transformation = JsPureApi.AsString(args, 0);
        var key = ToBytes(args.Length > 1 ? args[1] : null)
            ?? throw new ArgumentException("createSymmetricCrypto：key 不可为空");
        var iv = ToBytes(args.Length > 2 ? args[2] : null);
        var spec = CipherSpec.Parse(transformation, key);

        var currentIv = iv;
        var self = new JsObject(engine);

        // 闭包里的可变 IV：setIv 返回 this（legado SymmetricCryptoAndroid.setIv 同语义，支持链式）
        byte[] IvFor() => PadIv(currentIv, spec.BlockSize);

        object? Encrypt(object?[] a)
        {
            var data = Encoding.UTF8.GetBytes(JsPureApi.AsString(a, 0));
            return ToJsByteArray(engine, spec.Transform(encrypt: true, key, IvFor(), data));
        }

        self.FastSetDataProperty("encrypt", JsContextObjects.CreateFunction(engine, "encrypt", Encrypt, sink));
        self.FastSetDataProperty("encryptBase64", JsContextObjects.CreateFunction(engine, "encryptBase64", a =>
        {
            var data = Encoding.UTF8.GetBytes(JsPureApi.AsString(a, 0));
            var cipher = spec.Transform(encrypt: true, key, IvFor(), data);
            return Convert.ToBase64String(cipher);
        }, sink));
        self.FastSetDataProperty("encryptHex", JsContextObjects.CreateFunction(engine, "encryptHex", a =>
        {
            var data = Encoding.UTF8.GetBytes(JsPureApi.AsString(a, 0));
            var cipher = spec.Transform(encrypt: true, key, IvFor(), data);
            return Convert.ToHexString(cipher).ToLowerInvariant();
        }, sink));

        // 解密输入格式：reader 'auto' = 像十六进制就按 hex，否则按（宽松）base64；
        // aesBase64DecodeToString 这类"显式 Base64"旧 API 用 'base64'。
        self.FastSetDataProperty("decrypt", JsContextObjects.CreateFunction(engine, "decrypt", a =>
        {
            var cipher = spec.Transform(encrypt: false, key, IvFor(), DecodeInput(JsPureApi.AsString(a, 0), auto: true));
            return ToJsByteArray(engine, cipher);
        }, sink));
        self.FastSetDataProperty("decryptStr", JsContextObjects.CreateFunction(engine, "decryptStr", a =>
        {
            var cipher = spec.Transform(encrypt: false, key, IvFor(), DecodeInput(JsPureApi.AsString(a, 0), auto: true));
            return Encoding.UTF8.GetString(cipher);
        }, sink));

        self.FastSetDataProperty("setIv", JsContextObjects.CreateFunction(engine, "setIv", a =>
        {
            currentIv = ToBytes(a.Length > 0 ? a[0] : null);
            return self;
        }, sink));

        return self;
    }

    /// <summary><c>aesBase64DecodeToString(str, key, transformation, iv)</c>（输入必为 Base64）。</summary>
    public static object? AesBase64DecodeToString(object?[] args) =>
        DecryptExplicit(JsPureApi.AsString(args, 0), args, base64Input: true);

    /// <summary><c>aesDecodeToString(str, key, transformation, iv)</c>（输入 auto：hex 或 Base64）。</summary>
    public static object? AesDecodeToString(object?[] args) =>
        DecryptExplicit(JsPureApi.AsString(args, 0), args, base64Input: false);

    /// <summary><c>aesEncodeToBase64String(data, key, transformation, iv)</c>。</summary>
    public static object? AesEncodeToBase64String(object?[] args) =>
        EncryptExplicit(JsPureApi.AsString(args, 0), args, outputHex: false);

    /// <summary>
    /// <c>aesEncodeToString(data, key, transformation, iv)</c>。
    /// <para>legado 源码此处误写为 decryptStr（复制粘贴错误）；跟随 reader 修正为 encryptHex
    /// （样本 0 调用，纯为目录完整性而登记）。</para>
    /// </summary>
    public static object? AesEncodeToString(object?[] args) =>
        EncryptExplicit(JsPureApi.AsString(args, 0), args, outputHex: true);

    /// <summary><c>desEncodeToBase64String(data, key, transformation, iv)</c>（transformation 一般为 DES/DESede 前缀）。</summary>
    public static object? DesEncodeToBase64String(object?[] args) => AesEncodeToBase64String(args);

    /// <summary><c>desBase64DecodeToString(str, key, transformation, iv)</c>。</summary>
    public static object? DesBase64DecodeToString(object?[] args) => AesBase64DecodeToString(args);

    /// <summary><c>desDecodeToString(str, key, transformation, iv)</c>。</summary>
    public static object? DesDecodeToString(object?[] args) => AesDecodeToString(args);

    /// <summary><c>desEncodeToString(data, key, transformation, iv)</c> → hex（reader 修正语义）。</summary>
    public static object? DesEncodeToString(object?[] args) => AesEncodeToString(args);

    /// <summary><c>HMacHex(data, algorithm, key)</c> → 小写 hex（legado JsExtensions 同名）。</summary>
    public static object? HMacHex(object?[] args) =>
        HMac(JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1), JsPureApi.AsString(args, 2), outputHex: true);

    /// <summary><c>HMacBase64(data, algorithm, key)</c> → Base64。</summary>
    public static object? HMacBase64(object?[] args) =>
        HMac(JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1), JsPureApi.AsString(args, 2), outputHex: false);

    /// <summary><c>digestHex(data, algorithm)</c> → 小写 hex（"SHA-256"/"MD5"…）。</summary>
    public static object? DigestHex(object?[] args) =>
        Digest(JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1), outputHex: true);

    /// <summary><c>digestBase64Str(data, algorithm)</c> → Base64。</summary>
    public static object? DigestBase64Str(object?[] args) =>
        Digest(JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1), outputHex: false);

    // ============================================================
    // 对称加密核心（对标 reader symmetric_crypt / symmetric_codec）
    // ============================================================

    /// <summary>解析 Java <c>Cipher</c> 转换串：<c>AES|DES|DESede[/ECB|CBC[/PKCS5Padding|NoPadding|ZeroPadding]]</c>。</summary>
    private sealed class CipherSpec
    {
        public required string Algorithm { get; init; }
        public required CipherMode Mode { get; init; }
        public required PaddingMode Padding { get; init; }
        public required bool ZeroPadding { get; init; }
        public int BlockSize => Algorithm switch
        {
            "DES" or "DESEDE" => 8,
            _ => 16,
        };

        public static CipherSpec Parse(string transformation, byte[] key)
        {
            var parts = transformation.Split('/');
            var algorithm = (parts.Length > 0 ? parts[0] : string.Empty).Trim().ToUpperInvariant();
            var mode = (parts.Length > 1 ? parts[1] : "ECB").Trim().ToUpperInvariant() switch
            {
                "CBC" => CipherMode.CBC,
                "ECB" or "" => CipherMode.ECB,
                var other => throw new NotSupportedException($"不支持的加密模式：{other}"),
            };
            var paddingRaw = (parts.Length > 2 ? parts[2] : "PKCS5PADDING").Trim().ToUpperInvariant();
            var (padding, zero) = paddingRaw switch
            {
                "NOPADDING" => (PaddingMode.None, false),
                "ZEROPADDING" => (PaddingMode.None, true),
                // Java 的 PKCS5 对 AES 即 PKCS7（.NET 只有 PKCS7 一名）
                "PKCS5PADDING" or "PKCS7PADDING" or "ISO10126PADDING" => (PaddingMode.PKCS7, false),
                var other => throw new NotSupportedException($"不支持的填充：{other}"),
            };

            // 密钥长度校验提前报错（.NET 会抛更晦涩的异常）
            var expected = algorithm switch
            {
                "AES" => key.Length is 16 or 24 or 32 ? key.Length : -1,
                "DES" => key.Length,
                "DESEDE" or "TRIPLEDES" or "3DES" => key.Length,
                _ => throw new NotSupportedException($"不支持的加密算法：{algorithm}"),
            };
            if (algorithm is "AES" && expected == -1)
            {
                throw new InvalidOperationException($"AES 密钥长度无效：{key.Length}（须 16/24/32 字节）");
            }

            return new CipherSpec { Algorithm = algorithm, Mode = mode, Padding = padding, ZeroPadding = zero };
        }

        public byte[] Transform(bool encrypt, byte[] key, byte[] iv, byte[] data)
        {
            // DESede：16 字节密钥按 K1K2K1 扩展（Java SunJCE 拒 16 字节，书源生态里很常见，宽松处理）
            var effectiveKey = key;
            if (Algorithm is "DESEDE" or "TRIPLEDES" or "3DES" && key.Length == 16)
            {
                effectiveKey = new byte[24];
                Array.Copy(key, effectiveKey, 16);
                Array.Copy(key, 0, effectiveKey, 16, 8);
            }
            else if (Algorithm == "DES")
            {
                effectiveKey = key.Length > 8 ? key[..8] : key;
            }

            byte[] payload;
            if (encrypt)
            {
                payload = ZeroPadding ? PadZero(data, BlockSize) : data;
            }
            else
            {
                payload = data;
            }

            byte[] cipher;
            using (var alg = CreateAlgorithm())
            {
                alg.Key = NormalizeKey(alg, effectiveKey);
                alg.Mode = Mode;
                alg.Padding = ZeroPadding ? PaddingMode.None : Padding;
                if (Mode == CipherMode.CBC)
                {
                    alg.IV = iv;
                }

                using var transform = encrypt ? alg.CreateEncryptor() : alg.CreateDecryptor();
                cipher = transform.TransformFinalBlock(payload, 0, payload.Length);
            }

            if (!encrypt && ZeroPadding)
            {
                return TrimZero(cipher);
            }

            return cipher;
        }

        private SymmetricAlgorithm CreateAlgorithm() => Algorithm switch
        {
            "AES" => Aes.Create(),
            "DES" => System.Security.Cryptography.DES.Create(),
            _ => TripleDES.Create(),
        };

        // TripleDES 接受 16/24 字节（16 即 K1K2K1）；AES 16/24/32；DES 8
        private static byte[] NormalizeKey(SymmetricAlgorithm alg, byte[] key) => key;

        private static byte[] PadZero(byte[] data, int blockSize)
        {
            var remainder = data.Length % blockSize;
            if (remainder == 0)
            {
                return data;
            }

            var padded = new byte[data.Length + (blockSize - remainder)];
            Array.Copy(data, padded, data.Length);
            return padded;
        }

        private static byte[] TrimZero(byte[] data)
        {
            var end = data.Length;
            while (end > 0 && data[end - 1] == 0)
            {
                end--;
            }

            return data[..end];
        }
    }

    /// <summary>空/短 IV 补零到块长（reader 的 Node 向量：空 iv = 全零 IV）。</summary>
    private static byte[] PadIv(byte[]? iv, int blockSize)
    {
        if (iv is { Length: > 0 })
        {
            if (iv.Length >= blockSize)
            {
                return iv[..blockSize];
            }

            var padded = new byte[blockSize];
            Array.Copy(iv, padded, iv.Length);
            return padded;
        }

        return new byte[blockSize];
    }

    private static object? DecryptExplicit(string input, object?[] args, bool base64Input)
    {
        var transformation = JsPureApi.AsString(args, 2);
        var key = ToBytes(args.Length > 1 ? args[1] : null)
            ?? throw new ArgumentException("解密：key 不可为空");
        var iv = ToBytes(args.Length > 3 ? args[3] : null);
        var spec = CipherSpec.Parse(transformation, key);
        var cipher = spec.Transform(
            encrypt: false, key, PadIv(iv, spec.BlockSize), base64Input ? Base64DecodeLenient(input) : DecodeInput(input, auto: true));
        return Encoding.UTF8.GetString(cipher);
    }

    private static object? EncryptExplicit(string data, object?[] args, bool outputHex)
    {
        var transformation = JsPureApi.AsString(args, 2);
        var key = ToBytes(args.Length > 1 ? args[1] : null)
            ?? throw new ArgumentException("加密：key 不可为空");
        var iv = ToBytes(args.Length > 3 ? args[3] : null);
        var spec = CipherSpec.Parse(transformation, key);
        var cipher = spec.Transform(encrypt: true, key, PadIv(iv, spec.BlockSize), Encoding.UTF8.GetBytes(data));
        return outputHex
            ? Convert.ToHexString(cipher).ToLowerInvariant()
            : Convert.ToBase64String(cipher);
    }

    /// <summary>解密输入解码（reader 'auto'）：像 hex 就按 hex，否则按宽松 Base64。</summary>
    private static byte[] DecodeInput(string input, bool auto)
    {
        var trimmed = input.Trim();
        if (auto && trimmed.Length > 0 && trimmed.Length % 2 == 0 && IsHex(trimmed))
        {
            return Convert.FromHexString(trimmed);
        }

        return Base64DecodeLenient(trimmed);
    }

    /// <summary>
    /// 宽松 Base64：按长度补齐缺失的 <c>=</c> 后再解码。
    /// Android <c>android.util.Base64</c>/hutool（legado 底层）都接受缺省补位，.NET 不接受——
    /// 书源密文缺补位直接失败会丢正文（reader issue #13），这里对齐宽松语义。
    /// </summary>
    private static byte[] Base64DecodeLenient(string input)
    {
        var padded = input.Trim();
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    // ============================================================
    // HMAC / 摘要
    // ============================================================

    private static object? HMac(string data, string algorithm, string key, bool outputHex)
    {
        using var hmac = NormalizeAlgorithm(algorithm, stripHmacPrefix: true) switch
        {
            "MD5" => (HMAC)new HMACMD5(),
            "SHA1" => new HMACSHA1(),
            "SHA256" => new HMACSHA256(),
            "SHA384" => new HMACSHA384(),
            "SHA512" => new HMACSHA512(),
            var other => throw new NotSupportedException($"不支持的 HMAC 算法：{other}"),
        };
        hmac.Key = Encoding.UTF8.GetBytes(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return outputHex ? Convert.ToHexString(hash).ToLowerInvariant() : Convert.ToBase64String(hash);
    }

    private static object? Digest(string data, string algorithm, bool outputHex)
    {
        using var digest = NormalizeAlgorithm(algorithm, stripHmacPrefix: false) switch
        {
            "MD5" => (HashAlgorithm)MD5.Create(),
            "SHA1" => SHA1.Create(),
            "SHA256" => SHA256.Create(),
            "SHA384" => SHA384.Create(),
            "SHA512" => SHA512.Create(),
            var other => throw new NotSupportedException($"不支持的摘要算法：{other}"),
        };
        var hash = digest.ComputeHash(Encoding.UTF8.GetBytes(data));
        return outputHex ? Convert.ToHexString(hash).ToLowerInvariant() : Convert.ToBase64String(hash);
    }

    /// <summary>算法名归一："HmacMD5"→"MD5"、"SHA-256"→"SHA256"（Java 名两种写法都有）。</summary>
    private static string NormalizeAlgorithm(string algorithm, bool stripHmacPrefix)
    {
        var name = algorithm.Trim().ToUpperInvariant();
        if (stripHmacPrefix && name.StartsWith("HMAC", StringComparison.Ordinal))
        {
            name = name[4..];
        }

        return name.Replace("-", string.Empty).Replace("/", string.Empty);
    }

    // ============================================================
    // 字节序列（JS 数组形态，见类注释）
    // ============================================================

    /// <summary>参数 → 字节：JS 数组（数字 0-255）按字节取；字符串按 UTF-8（文本 key/iv/data）。</summary>
    private static byte[]? ToBytes(object? value) => value switch
    {
        null => null,
        byte[] b => b,
        object?[] array => BytesFromArray(array),
        string s => Encoding.UTF8.GetBytes(s),
        _ => Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty),
    };

    private static byte[] BytesFromArray(object?[] array)
    {
        var bytes = new byte[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            bytes[i] = (byte)(Convert.ToInt64(array[i]) & 0xFF);
        }

        return bytes;
    }

    /// <summary>字节 → JS 数组（数字 0-255），与 <c>base64DecodeToByteArray</c> 的返回形态一致。</summary>
    private static JsValue ToJsByteArray(Engine engine, byte[] bytes)
    {
        var array = new JsArray(engine, Array.Empty<JsValue>());
        foreach (var b in bytes)
        {
            array.Push((double)b);
        }

        return array;
    }
}

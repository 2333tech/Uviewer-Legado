using System;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-5b：T0 加解密原语（<c>createSymmetricCrypto/aes*/des*/HMac*/digest*/base64DecodeToByteArray</c>）。
///
/// <para><b>期望值来源</b>：reader <c>js.rs</c> 测试里由 Node <c>crypto</c> 生成的硬编码向量
/// （等价 Java <c>Cipher</c> PKCS5Padding），保证与 Java/legado 字节级一致，而非用本实现自证。
/// 失败语义：解密失败/密钥非法 → 异常被 <see cref="JsContextObjects.CreateFunction"/> 吞掉记诊断返回 null。</para>
/// </summary>
public class Stage4CryptoTests : IDisposable
{
    private readonly JsDiagnostics _diagnostics = new();
    private readonly JsBridge _bridge;

    public Stage4CryptoTests()
    {
        _bridge = new JsBridge
        {
            Diagnostics = _diagnostics,
            SourceKey = "crypto-test",
            Field = "content",
        };
    }

    public void Dispose() => _diagnostics.Clear();

    private string Run(string script) =>
        _bridge.Run(script, "body", "https://example.com/book/1.html");

    // ============================================================
    // 对称加密（reader 的 Node 向量）
    // ============================================================

    [Fact]
    public void CreateSymmetricCrypto_Desede_Cbc_Matches_Node_Vector()
    {
        var encrypted = Run(
            "java.createSymmetricCrypto('DESede/CBC/PKCS5Padding','0821CAAD409B84020821CAAD','').encryptBase64('测试文本')");
        Assert.Equal("tJzEHmM1hTDNyM9hQ+3rdQ==", encrypted);

        var decrypted = Run(
            "java.createSymmetricCrypto('DESede/CBC/PKCS5Padding','0821CAAD409B84020821CAAD','')" +
            ".decryptStr('tJzEHmM1hTDNyM9hQ+3rdQ==')");
        Assert.Equal("测试文本", decrypted);
    }

    [Fact]
    public void CreateSymmetricCrypto_Aes_Ecb_Matches_Node_Vector()
    {
        Assert.Equal(
            "008a30f11b63572c2da1fad67b18eb66",
            Run("java.createSymmetricCrypto('AES/ECB/PKCS5Padding','1234567890abcdef','').encryptHex('abc')"));
    }

    [Fact]
    public void Desede_16_Byte_Key_Expands_K1K2K1()
    {
        // SunJCE 拒 16 字节 3DES 密钥；书源生态常见，宽松按 K1K2K1 扩展（reader 同）
        Assert.Equal(
            "qm5j84FYbv8=",
            Run("java.createSymmetricCrypto('DESede/CBC/PKCS5Padding','0123456789abcdef','').encryptBase64('hello')"));
    }

    [Fact]
    public void Aes_192_And_256_Keys()
    {
        Assert.Equal(
            "377f35478afd40126bdcb4d5f9be9ee6",
            Run("java.createSymmetricCrypto('AES/ECB/PKCS5Padding','0123456789abcdef01234567','').encryptHex('abc')"));
        Assert.Equal(
            "9d7ab829be8b28fd0d1f1568d8842e00",
            Run("java.createSymmetricCrypto('AES/ECB/PKCS5Padding','0123456789abcdef0123456789abcdef','').encryptHex('abc')"));
    }

    [Fact]
    public void NoPadding_Keeps_Block_Aligned_Input()
    {
        Assert.Equal(
            "95b012b0bc898e5c37eeed6588635f09",
            Run("java.createSymmetricCrypto('AES/ECB/NoPadding','1234567890abcdef','').encryptHex('1234567890abcdef')"));
        Assert.Equal(
            "OI3Pry69kmw=",
            Run("java.createSymmetricCrypto('DESede/CBC/NoPadding','0123456789abcdef','').encryptBase64('12345678')"));
    }

    [Fact]
    public void Invalid_Key_Length_Returns_Null_And_Records()
    {
        // 密钥非法：createSymmetricCrypto 内部异常 → CreateFunction 记诊断返回 null；
        // 后续 .encryptHex 链在 JS 层抛 TypeError → Run 兜底返回原文（不能假装成功）
        Assert.Equal("null", Run("String(java.createSymmetricCrypto('AES/ECB/PKCS5Padding','short',''))"));
        Assert.Contains(_diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.ScriptError);
    }

    // ============================================================
    // 旧 API 形态（样本真实参数序：data, key, transformation, iv）
    // ============================================================

    [Fact]
    public void AesBase64DecodeToString_Round_Trip()
    {
        // 样本原始形态：java.aesBase64DecodeToString(result, "f041c49714d39908", "AES/CBC/PKCS5Padding", "0123456789abcdef")
        var encrypted = Run(
            "java.createSymmetricCrypto('AES/CBC/PKCS5Padding','f041c49714d39908','0123456789abcdef').encryptBase64('章节内容测试')");
        var decrypted = Run(
            $"'{encrypted}'.length > 0 ? java.aesBase64DecodeToString('{encrypted}','f041c49714d39908','AES/CBC/PKCS5Padding','0123456789abcdef') : ''");
        Assert.Equal("章节内容测试", decrypted);
    }

    [Fact]
    public void Des_Encode_Decode_Aliases()
    {
        var encrypted = Run("java.desEncodeToBase64String('utId=996','snY%169j','DES/CBC/PKCS5Padding','12345678')");
        Assert.Equal("utId=996", Run($"java.desBase64DecodeToString('{encrypted}','snY%169j','DES/CBC/PKCS5Padding','12345678')"));
    }

    // ============================================================
    // 字节数组 key/iv（样本真实链路：base64DecodeToByteArray → createSymmetricCrypto）
    // ============================================================

    [Fact]
    public void ByteArray_Key_And_Iv_Round_Trip()
    {
        var script = """
            var key = java.base64DecodeToByteArray('TH5TMirKqAUhSTEFBR1bUw==');
            var iv = java.base64DecodeToByteArray('AAAAAAAAAAAAAAAAAAAAAA==');
            var cipher = java.createSymmetricCrypto('AES/CBC/PKCS7Padding', key, iv);
            var enc = cipher.encryptBase64('正文');
            cipher.decryptStr(enc)
            """;
        Assert.Equal("正文", Run(script));
    }

    [Fact]
    public void Hex_Decode_To_ByteArray()
    {
        Assert.Equal("2|255", Run("var b = java.hexDecodeToByteArray('08ff'); b.length + '|' + b[1]"));
        Assert.Equal("null", Run("String(java.hexDecodeToByteArray('0g'))"));
    }

    [Fact]
    public void Lenient_Base64_Pads_Missing_Padding()
    {
        // 22 字符缺 == 的 Base64（reader issue #13：Android 宽松、.NET 严格）→ 16 字节
        Assert.Equal("16", Run("java.base64DecodeToByteArray('mPgPm6AQ/x1kMQYOBuq9qw').length"));
    }

    // ============================================================
    // HMAC / 摘要（RFC 2202 / NIST 标准向量）
    // ============================================================

    [Fact]
    public void HMacHex_Matches_Rfc_Vector()
    {
        // HMAC-MD5("The quick brown fox jumps over the lazy dog", "key") = 80070713463e7749b90c2dc24911e275
        Assert.Equal(
            "80070713463e7749b90c2dc24911e275",
            Run("java.HMacHex('The quick brown fox jumps over the lazy dog','HmacMD5','key')"));
    }

    [Fact]
    public void DigestHex_Matches_Standard_Vector()
    {
        // SHA-256("abc") = ba7816bf...
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Run("java.digestHex('abc','SHA-256')"));
        Assert.Equal(
            "900150983cd24fb0d6963f7d28e17f72",
            Run("java.digestHex('abc','MD5')"));
    }
}

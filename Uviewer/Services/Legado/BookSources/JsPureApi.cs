using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// T0「纯函数」原语实现（阶段 4-1）：不依赖网络、宿主状态与 WebView，只做字符串/时间/编码/摘要变换。
/// <para><b>为什么先做这一层</b>：原始样本里 <c>java.log</c>（499 次 / 61 源）、<c>java.md5Encode</c>
/// （351 次 / 24 源）、<c>java.toast</c>（181 次 / 50 源）都属本层。它们本身不"抓数据"，
/// 但**缺失时会抛错让整段脚本作废**（<see cref="JsBridge.Run"/> 按 legado 语义回退原文），
/// 于是目录/正文静默变错。补齐本层是性价比最高的一步。</para>
/// <para>语义锚点：legado <c>JsExtensions.kt</c> / <c>JsEncodeUtils.kt</c> / <c>StringUtils.kt</c>，
/// 每个方法都注明对应的 Kotlin 实现，避免"看起来对但边界不同"。</para>
/// <para>纯 C#，不引用 Jint：参数已由 <see cref="JsBridge"/> 转成 CLR 对象，故可单测。</para>
/// </summary>
internal static class JsPureApi
{
    /// <summary>legado <c>AppConst.dateFormat</c>：<c>yyyy/MM/dd HH:mm</c>。</summary>
    private const string DefaultDateFormat = "yyyy/MM/dd HH:mm";

    /// <summary>
    /// 执行一个 T0 原语。<paramref name="name"/> 必须是 <see cref="JsApiCatalog"/> 里状态为
    /// Implemented 的名字；返回 false 表示本层不处理（调用方按桩/缺失处理）。
    /// </summary>
    public static bool TryInvoke(string name, object?[] args, out object? result)
    {
        switch (name)
        {
            case "md5":
            case "md5Encode":
                result = Md5Hex(AsString(args, 0));
                return true;

            case "md5Encode16":
                // legado MD5Utils.md5Encode16：32 位 hex 取 substring(8, 24)
                var hex = Md5Hex(AsString(args, 0));
                result = hex.Length >= 24 ? hex.Substring(8, 16) : hex;
                return true;

            case "base64Encode":
                result = Convert.ToBase64String(Encoding.UTF8.GetBytes(AsString(args, 0)));
                return true;

            case "base64Decode":
                result = Base64Decode(AsString(args, 0));
                return true;

            case "timeFormat":
                // legado：java.timeFormat(time: Long) → AppConst.dateFormat（本地时区）
                result = FormatLocal(AsLong(args, 0), DefaultDateFormat);
                return true;

            case "timeFormatUTC":
                // legado：java.timeFormatUTC(time, format, sh)（format/sh 可选，sh = 时区小时偏移）
                result = FormatUtc(
                    AsLong(args, 0),
                    args.Length > 1 && args[1] is not null ? AsString(args, 1) : DefaultDateFormat,
                    args.Length > 2 ? (int)AsLong(args, 2) : 0);
                return true;

            case "encodeURI":
                // legado：URLEncoder.encode(str, "UTF-8")（空格 → '+'）
                result = WebUtility.UrlEncode(AsString(args, 0));
                return true;

            case "hexDecodeToString":
                result = HexDecodeToString(AsString(args, 0));
                return true;

            case "hexEncodeToString":
                result = HexEncodeToString(AsString(args, 0));
                return true;

            case "toNumChapter":
                result = ToNumChapter(AsString(args, 0));
                return true;

            case "getUserAgent":
            case "getWebViewUA":
                result = HttpBookSourceFetcher.DefaultUserAgent;
                return true;

            case "randomUUID":
                // legado java.randomUUID()：无连字符的 32 位小写（脚本常 replace("-") 去连字符）
                result = Guid.NewGuid().ToString("D");
                return true;

            default:
                result = null;
                return false;
        }
    }

    // ============================================================
    // 摘要 / 编码
    // ============================================================

    /// <summary>UTF-8 MD5，小写 32 位 hex（legado <c>MD5Utils.md5Encode</c>）。</summary>
    private static string Md5Hex(string text)
    {
        // MD5.HashData 静态 API（net7+）：免去每次 Create() 的分配；
        // Convert.ToHexStringLower 免掉 ToLowerInvariant 的二次分配。
        // md5Encode 在样本里出现 351 次，是书源最高频的加解密原语。
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>UTF-8 hex 小写（legado <c>hexEncodeToString</c>）。</summary>
    private static string HexEncodeToString(string text) =>
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text));

    /// <summary>Base64 → UTF-8；非法输入返回空串（legado 侧异常会抛，这里对齐本引擎既有 <c>base64Decode</c> 语义）。</summary>
    private static string Base64Decode(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>hex → UTF-8 字符串；奇数长度/非法字符按"能解多少解多少"处理（legado 用 hexToByteArray，非法即抛）。</summary>
    private static string HexDecodeToString(string hex)
    {
        var clean = hex.Trim();
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        if (clean.Length % 2 != 0)
        {
            // 奇数长度：末尾补 0，避免整段报废（真实书源里偶见截断的 hex）
            clean += "0";
        }

        var bytes = new byte[clean.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(clean.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return string.Empty;
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    // ============================================================
    // 时间
    // ============================================================

    /// <summary>毫秒时间戳 → 本地时区格式化（legado <c>dateFormat.format(Date(time))</c>）。</summary>
    private static string FormatLocal(long milliseconds, string javaPattern)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
        return time.ToString(ToDotNetPattern(javaPattern), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 毫秒时间戳 → 指定时区格式化。
    /// <para>legado：<c>SimpleTimeZone(sh, "UTC")</c>，即 UTC + sh 小时（sh 可为负）。</para>
    /// </summary>
    private static string FormatUtc(long milliseconds, string javaPattern, int offsetHours)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToOffset(TimeSpan.FromHours(offsetHours));
        return time.ToString(ToDotNetPattern(javaPattern), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Java <c>SimpleDateFormat</c> 模式 → .NET 自定义格式串（只覆盖书源实际用到的 token）。
    /// <para>未识别的字母原样保留（Java 的转义字符 <c>'</c> 段也原样保留），宁可格式略偏也不抛异常。</para>
    /// </summary>
    private static string ToDotNetPattern(string javaPattern)
    {
        if (string.IsNullOrEmpty(javaPattern))
        {
            return DefaultDateFormat;
        }

        var sb = new StringBuilder(javaPattern.Length + 4);
        var i = 0;
        while (i < javaPattern.Length)
        {
            var c = javaPattern[i];
            var run = 1;
            while (i + run < javaPattern.Length && javaPattern[i + run] == c)
            {
                run++;
            }

            switch (c)
            {
                case 'y':
                    sb.Append(run >= 4 ? "yyyy" : "yy");
                    break;
                case 'M':
                    sb.Append(run >= 2 ? "MM" : "M");
                    break;
                case 'd':
                    sb.Append(run >= 2 ? "dd" : "d");
                    break;
                case 'H':
                    sb.Append(run >= 2 ? "HH" : "H");
                    break;
                case 'h':
                    sb.Append(run >= 2 ? "hh" : "h");
                    break;
                case 'm':
                    sb.Append(run >= 2 ? "mm" : "m");
                    break;
                case 's':
                    sb.Append(run >= 2 ? "ss" : "s");
                    break;
                case 'S':
                    // Java 的 S 是毫秒；.NET 是 fff
                    sb.Append("fff");
                    break;
                case 'E':
                    sb.Append("ddd");
                    break;
                case 'a':
                    sb.Append("tt");
                    break;
                default:
                    // 未识别字母：单引号转义成字面量。原样保留会让 .NET 自定义
                    // 格式串把 'Z'/'D'/'w' 当格式说明符解析 → FormatException
                    // → 整段脚本回退原文（注释承诺"宁可格式略偏也不抛异常"，
                    // 原实现恰恰会抛）。
                    if (char.IsLetter(c))
                    {
                        sb.Append('\'').Append(c, run).Append('\'');
                    }
                    else
                    {
                        sb.Append(c, run);
                    }

                    break;
            }

            i += run;
        }

        return sb.ToString();
    }

    // ============================================================
    // 章节号归一（legado JsExtensions.toNumChapter + StringUtils.stringToInt）
    // ============================================================

    /// <summary>legado <c>AppPattern.titleNumPattern</c>：<c>(第)(.+?)(章)</c>。</summary>
    private static readonly Regex TitleNumPattern = new(@"(第)(.+?)(章)", RegexOptions.CultureInvariant);

    /// <summary>
    /// 章节名里的中文数字归一：<c>第一百二十三章</c> → <c>第123章</c>。
    /// <para>legado：匹配 <c>第(.+?)章</c>，把中间段转数字后拼回；不是数字时原样返回；
    /// 完全匹配不到也原样返回（<b>不返回 null</b>，否则脚本里字符串拼接会变成 "null"）。</para>
    /// </summary>
    private static string ToNumChapter(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var match = TitleNumPattern.Match(text);
        if (!match.Success)
        {
            return text;
        }

        var number = StringToInt(match.Groups[2].Value);
        return number < 0
            ? text
            : string.Concat(match.Groups[1].Value, number.ToString(CultureInfo.InvariantCulture), match.Groups[3].Value);
    }

    /// <summary>legado <c>StringUtils.stringToInt</c>：全角转半角 → 去空白 → 先试十进制，再试中文数字；失败 -1。</summary>
    private static int StringToInt(string text)
    {
        var normalized = FullToHalf(text).Trim();
        if (normalized.Length == 0)
        {
            return -1;
        }

        // 去掉内部空白（legado 用 replace("\\s+".toRegex(), "")）
        if (normalized.Any(char.IsWhiteSpace))
        {
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            normalized = sb.ToString();
        }

        if (int.TryParse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        return ChineseNumToInt(normalized);
    }

    /// <summary>全角 → 半角（legado <c>StringUtils.fullToHalf</c>：U+3000 空格 + U+FF01..U+FF5E 区间）。</summary>
    private static string FullToHalf(string input)
    {
        var chars = input.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\u3000')
            {
                chars[i] = ' ';
            }
            else if (chars[i] >= '\uFF01' && chars[i] <= '\uFF5E')
            {
                chars[i] = (char)(chars[i] - 0xFEE0);
            }
        }

        return new string(chars);
    }

    /// <summary>中文数字字符 → 数值（legado <c>StringUtils.chnMap</c>）。</summary>
    private static readonly Dictionary<char, int> ChineseDigits = new(26)
    {
        ['零'] = 0, ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10,
        ['〇'] = 0, ['壹'] = 1, ['贰'] = 2, ['叁'] = 3, ['肆'] = 4,
        ['伍'] = 5, ['陆'] = 6, ['柒'] = 7, ['捌'] = 8, ['玖'] = 9, ['拾'] = 10,
        ['两'] = 2,
        ['百'] = 100, ['佰'] = 100,
        ['千'] = 1000, ['仟'] = 1000,
        ['万'] = 10000,
        ['亿'] = 100000000,
    };

    /// <summary>纯数字序列（"一零二五" 形式）的判定：全部字符都是 0~10 的数码。</summary>
    private static bool IsDigitSequence(string text) =>
        text.Length > 1 && text.All(c => ChineseDigits.TryGetValue(c, out var v) && v <= 10);

    /// <summary>
    /// 中文数字 → int（逐行对齐 legado <c>StringUtils.chineseNumToInt</c>）。
    /// <para>支持 <c>一零二五</c> 这种逐位写法与 <c>一千零二十五</c> / <c>一千二</c> 这种带量级写法；
    /// 解析失败返回 -1（与 legado 的 <c>getOrDefault(-1)</c> 一致）。</para>
    /// </summary>
    private static int ChineseNumToInt(string text)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        try
        {
            // ① 逐位写法："一零二五" → 1025
            if (IsDigitSequence(text))
            {
                var digits = new char[text.Length];
                for (var i = 0; i < text.Length; i++)
                {
                    digits[i] = (char)('0' + ChineseDigits[text[i]]);
                }

                return int.TryParse(new string(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                    ? seq
                    : -1;
            }

            // ② 带量级写法
            var result = 0;
            var tmp = 0;
            var billion = 0;
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!ChineseDigits.TryGetValue(chars[i], out var value))
                {
                    return -1;
                }

                if (value == 100000000)
                {
                    result += tmp;
                    result *= value;
                    billion = billion * 100000000 + result;
                    result = 0;
                    tmp = 0;
                }
                else if (value == 10000)
                {
                    result += tmp;
                    result *= value;
                    tmp = 0;
                }
                else if (value >= 10)
                {
                    if (tmp == 0)
                    {
                        tmp = 1;
                    }

                    result += value * tmp;
                    tmp = 0;
                }
                else
                {
                    // "一千二" 的末尾省略量级：末位数字乘前一位量级的 1/10
                    tmp = i >= 2 && i == chars.Length - 1 && ChineseDigits[chars[i - 1]] > 10
                        ? value * ChineseDigits[chars[i - 1]] / 10
                        : tmp * 10 + value;
                }
            }

            return result + tmp + billion;
        }
        catch
        {
            return -1;
        }
    }

    // ============================================================
    // 参数转换
    // ============================================================

    /// <summary>
    /// 取第 <paramref name="index"/> 个参数为字符串。
    /// <para>JS 里 <c>undefined</c>/<c>null</c> 应表现为空串（书源脚本常直接拼进 URL），
    /// 数字按不变文化输出（避免 <c>1,5</c> 这类本地化小数点）；
    /// 布尔输出 <c>true</c>/<c>false</c> 小写——.NET 的 <c>bool.ToString()</c> 是
    /// <c>True</c>/<c>False</c>，与 JS/Java 口径不一致（书源写
    /// <c>java.put("k", true)</c> 后 <c>java.get("k") === 'true'</c> 会恒失败）。</para>
    /// </summary>
    internal static string AsString(object?[] args, int index)
    {
        if (args.Length <= index || args[index] is null)
        {
            return string.Empty;
        }

        return args[index] switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => args[index]!.ToString() ?? string.Empty,
        };
    }

    /// <summary>取第 <paramref name="index"/> 个参数为 long（时间戳常见 double/字符串两种形态）。</summary>
    internal static long AsLong(object?[] args, int index)
    {
        if (args.Length <= index || args[index] is null)
        {
            return 0;
        }

        return args[index] switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            decimal m => (long)m,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => (long)parsed,
            bool b => b ? 1 : 0,
            _ => 0,
        };
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Interop;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// legado 脚本的<b>全局对象</b>：<c>source</c> / <c>book</c> / <c>chapter</c> / <c>cache</c> /
/// <c>cookie</c> / <c>html</c>（阶段 4-2 新增）。
///
/// <para><b>语义来源（按优先级）</b>：</para>
/// <list type="number">
/// <item>legado 真实实现：<c>BookSource</c>/<c>Book</c>/<c>BookChapter</c> 实体
///   + <c>CookieStore</c>/<c>CacheManager</c> + <c>AnalyzeRule</c> 的 <c>put/get</c> 三级作用域；</item>
/// <item>reader 的权威移植：<c>src/parser/js.rs</c> 的 JS prelude（<c>source.*</c>/<c>book.*</c>/
///   <c>cache.*</c>/<c>cookie.*</c> 的成员表与键前缀 <c>sv:/sp:/bv:/cv:/file:/brc:</c> 全部照抄）；</item>
/// <item>原始样本 408 条的实调用统计（哪些成员真的有人用、调用形态长什么样）。</item>
/// </list>
///
/// <para><b>与 reader 的两处有意增强</b>：</para>
/// <list type="bullet">
/// <item><c>loginInfoMap</c>/<c>loginHeaderMap</c> 返回<b>带 <c>get/put/has</c></c> 的普通 JS 对象：
///   legado 里它们是 Java <c>Map</c>，书源会写 <c>loginInfoMap.get('源作者')</c>（reader 直接返回
///   <c>JSON.parse</c> 的裸对象，这种写法会抛错）。这里补三个方法，两种写法都能跑。</item>
/// <item>未知成员不再抛 <c>TypeError</c>：每个对象都套 Proxy 兜底（记诊断 + 返回 null），
///   与 <see cref="JsBridge"/> 对 <c>java</c> 的处理一致 —— 机制上消灭"一个笔误废掉整段脚本"。</item>
/// </list>
///
/// <para><b><c>html</c> 是记录型空桩</b>：legado 的 JS 绑定里<b>没有</b> <c>html</c> 全局对象
/// （只有 <c>java</c>/<c>cookie</c>/<c>cache</c>/<c>source</c>/<c>book</c> 等）。原始样本里那 15 处
/// <c>html.match(…)</c> 经逐条核对<b>全部是脚本内的局部变量</b>（<c>let html = java.ajax(…)</c>）。
/// 故这里只放一个"访问即记诊断、返回 null"的空对象：既不让真正的全局 <c>html.xxx</c> 写法抛错，
/// 也不假装实现了不存在的语义（<c>JsGlobalCatalog</c> 也不把它算作能力门槛）。</para>
/// </summary>
internal static class JsContextObjects
{
    /// <summary>Proxy 兜底回调名序号（全进程自增，避免同名全局互相覆盖）。</summary>
    private static int _proxySeq;

    /// <summary>
    /// 把六个全局对象绑定到引擎。
    /// <para>每次换 <see cref="JsScope"/>（换链/换书）都要重新绑定：对象里的数据属性是
    /// 绑定时快照（<c>book.name</c> 这类），函数则闭包住本次 scope。</para>
    /// <para>诊断接收器用 <paramref name="diagnostics"/>（= <see cref="JsBridge.Diagnostics"/>），
    /// 不用 <see cref="JsScope.Diagnostics"/>：单测可能直接给 bridge 注入诊断实例，
    /// 两者在生产路径上是同一个对象，但显式传参才不会出现"诊断记到 Shared 里去了"。</para>
    /// </summary>
    public static void Bind(Engine engine, JsScope scope, JsDiagnostics diagnostics, string? field)
    {
        var sink = new JsErrorSink(diagnostics, scope.SourceKey, field);
        Action<string> missing = member => diagnostics.RecordMissingApi(scope.SourceKey, field, member);

        engine.SetValue("source", BuildSource(engine, scope, sink, missing));
        engine.SetValue("book", BuildBook(engine, scope, sink, missing));
        engine.SetValue("chapter", BuildChapter(engine, scope, sink, missing));
        engine.SetValue("cache", BuildCache(engine, scope, sink, missing));
        engine.SetValue("cookie", BuildCookie(engine, scope, sink, missing));
        engine.SetValue("html", BuildHtmlStub(engine, missing));
    }

    // ============================================================
    // chapter
    // ============================================================

    /// <summary>
    /// <c>chapter</c>：当前章节（legado 的 <c>AnalyzeRule.chapter</c>）。
    /// <para><b>为什么 4-2 顺带做了它</b>：原始样本里有约 10 个视频类书源的正文规则写
    /// <c>if (book.durChapterIndex == chapter.index) { java.startBrowser(...) }</c>——
    /// <c>book</c> 注入后若 <c>chapter</c> 仍不存在，脚本会抛 <c>ReferenceError</c>，
    /// 又被 <see cref="JsBridge.Run"/> 兜底成"返回原文"，等于半个修复。
    /// 章节变量（<c>java.put</c> 的 <c>cv:</c> 作用域）本来就在 <see cref="JsScope"/> 里，
    /// 这里只是把同一份数据以 legado 的字段名暴露出去。</para>
    /// </summary>
    private static JsValue BuildChapter(Engine engine, JsScope scope, JsErrorSink sink, Action<string> missing)
    {
        var chapterUrl = scope.ChapterUrl ?? string.Empty;
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("title", ToJs(engine, scope.Book?.DurChapterTitle ?? string.Empty));
        obj.FastSetDataProperty("url", ToJs(engine, chapterUrl));
        obj.FastSetDataProperty("index", ToJs(engine, scope.Book?.DurChapterIndex ?? 0));
        obj.FastSetDataProperty("getKey", CreateFunction(engine, "getKey", _ => chapterUrl, sink));
        obj.FastSetDataProperty("getVariable", CreateFunction(engine, "getVariable", args =>
        {
            if (scope.ChapterUrl is null)
            {
                return string.Empty;
            }

            return scope.Read(scope.ChapterKey(JsPureApi.AsString(args, 0))) ?? string.Empty;
        }, sink));
        var put = CreateFunction(engine, "putVariable", args =>
        {
            var key = JsPureApi.AsString(args, 0);
            var value = JsPureApi.AsString(args, 1);
            if (scope.ChapterUrl is not null)
            {
                WriteState(scope, sink, scope.ChapterKey(key), value, "chapter.putVariable");
            }
            else if (!string.IsNullOrEmpty(scope.Book?.BookUrl))
            {
                WriteState(scope, sink, scope.BookKey(key), value, "chapter.putVariable");
            }

            return true;
        }, sink);
        obj.FastSetDataProperty("putVariable", put);
        obj.FastSetDataProperty("setVariable", put);

        return WrapWithMissingFallback(engine, obj, "chapter.", missing);
    }

    // ============================================================
    // source
    // ============================================================

    /// <summary>
    /// <c>source</c>：书源本身。数据属性取 <see cref="Models.BookSource"/>；变量/登录信息落到
    /// <see cref="SourceStateStore"/> 的 <c>sv:/sp:</c> 键。
    /// <para>注意 <c>source.key</c>/<c>getKey()</c> 返回的是<b>书源地址</b>（不是书籍地址）——
    /// 书源常用 <c>source.getKey().replace('//s','//chapter')</c> 拼章节域名，
    /// 书籍地址在 legado 里走 <c>book.bookUrl</c>（reader 的 issue #13 就是这里搞混）。
    /// </para>
    /// </summary>
    private static JsValue BuildSource(Engine engine, JsScope scope, JsErrorSink sink, Action<string> missing)
    {
        var model = scope.Source;
        var sourceKey = scope.SourceKey;
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("key", ToJs(engine, sourceKey));
        obj.FastSetDataProperty("bookSourceUrl", ToJs(engine, sourceKey));
        obj.FastSetDataProperty("bookSourceName", ToJs(engine, model?.BookSourceName ?? string.Empty));
        obj.FastSetDataProperty("bookSourceComment", ToJs(engine, model?.BookSourceComment ?? string.Empty));
        obj.FastSetDataProperty("bookSourceGroup", ToJs(engine, model?.BookSourceGroup ?? string.Empty));
        obj.FastSetDataProperty("bookSourceType", ToJs(engine, model?.BookSourceType ?? 0));
        obj.FastSetDataProperty("variableComment", ToJs(engine, model?.VariableComment ?? string.Empty));
        obj.FastSetDataProperty("loginUrl", ToJs(engine, model?.LoginUrl ?? string.Empty));
        obj.FastSetDataProperty("header", ToJs(engine, model?.Header ?? string.Empty));
        obj.FastSetDataProperty("lastUpdateTime", ToJs(engine, model?.LastUpdateTime ?? 0L));

        obj.FastSetDataProperty("getKey", CreateFunction(engine, "getKey", _ => sourceKey, sink));

        // 书源变量：legado 是**一整个字符串**（JSON 常驻），reader 存 sv:variable。
        // 与 legado/reader 一致：忽略参数（样本里 54 处 getVariable() 全部无参）。
        obj.FastSetDataProperty("getVariable", CreateFunction(engine, "getVariable",
            _ => scope.Read(scope.SourceMetaKey("variable")) ?? string.Empty, sink));

        var setVariable = CreateFunction(engine, "setVariable", args =>
        {
            WriteState(scope, sink, scope.SourceMetaKey("variable"), JsPureApi.AsString(args, 0), "source.setVariable");
            return null;
        }, sink);
        obj.FastSetDataProperty("setVariable", setVariable);
        obj.FastSetDataProperty("putVariable", setVariable);

        // source.put/get：书源级键值（reader 前缀 sp:）
        obj.FastSetDataProperty("put", CreateFunction(engine, "put", args =>
        {
            var key = JsPureApi.AsString(args, 0);
            var value = JsPureApi.AsString(args, 1);
            WriteState(scope, sink, scope.SourcePutKey(key), value, "source.put");
            return value;
        }, sink));
        obj.FastSetDataProperty("get", CreateFunction(engine, "get",
            args => scope.Read(scope.SourcePutKey(JsPureApi.AsString(args, 0))) ?? string.Empty, sink));

        // 登录信息（loginUrl 脚本里 putLoginInfo，阅读链里 getLoginInfoMap 取 token 一类的值）
        obj.FastSetDataProperty("getLoginInfo", CreateFunction(engine, "getLoginInfo",
            _ => (object?)scope.Read(scope.SourceMetaKey("loginInfo")), sink));
        obj.FastSetDataProperty("getLoginInfoMap", CreateFunction(engine, "getLoginInfoMap",
            _ => ParseJsonWithMapAccessors(engine, scope.Read(scope.SourceMetaKey("loginInfo"))), sink));
        obj.FastSetDataProperty("putLoginInfo", CreateFunction(engine, "putLoginInfo", args =>
        {
            WriteState(scope, sink, scope.SourceMetaKey("loginInfo"), StringifyArg(args, 0), "source.putLoginInfo");
            return null;
        }, sink));
        obj.FastSetDataProperty("removeLoginInfo", CreateFunction(engine, "removeLoginInfo", _ =>
        {
            scope.Delete(scope.SourceMetaKey("loginInfo"));
            return null;
        }, sink));

        // 登录请求头：legado 里 putLoginHeader 会把 Cookie 同步进 Cookie 仓库
        obj.FastSetDataProperty("getLoginHeader", CreateFunction(engine, "getLoginHeader",
            _ => (object?)scope.Read(scope.SourceMetaKey("loginHeader")), sink));
        obj.FastSetDataProperty("getLoginHeaderMap", CreateFunction(engine, "getLoginHeaderMap",
            _ => ParseJsonWithMapAccessors(engine, scope.Read(scope.SourceMetaKey("loginHeader"))), sink));
        obj.FastSetDataProperty("putLoginHeader", CreateFunction(engine, "putLoginHeader", args =>
        {
            var text = StringifyArg(args, 0);
            WriteState(scope, sink, scope.SourceMetaKey("loginHeader"), text, "source.putLoginHeader");
            JsCookieJar.MergeCookieHeader(scope, sourceKey, text);
            return null;
        }, sink));
        obj.FastSetDataProperty("removeLoginHeader", CreateFunction(engine, "removeLoginHeader", _ =>
        {
            scope.Delete(scope.SourceMetaKey("loginHeader"));
            JsCookieJar.RemoveCookie(scope, sourceKey);
            return null;
        }, sink));

        // 宿主能力（刷新发现页/重跑登录/刷新 jsLib）：4-2 无宿主落点，记录诊断并按 legado 返回成功
        obj.FastSetDataProperty("refreshExplore", Stub(engine, missing, "source.refreshExplore", true, sink));
        obj.FastSetDataProperty("login", Stub(engine, missing, "source.login", true, sink));
        obj.FastSetDataProperty("refreshJSLib", Stub(engine, missing, "source.refreshJSLib", true, sink));

        return WrapWithMissingFallback(engine, obj, "source.", missing);
    }

    // ============================================================
    // book
    // ============================================================

    /// <summary>
    /// <c>book</c>：当前书籍。数据属性来自 <see cref="JsBookState"/>；变量落 <c>bv:</c> 键。
    /// <para>正文链里 <c>book.name</c>（55 个源用到）与目录链里 <c>book.durChapterIndex</c>
    /// 是 4-2 最直接的收益：此前整段脚本抛 <c>ReferenceError: book is not defined</c>，
    /// 被 <see cref="JsBridge.Run"/> 兜底成"返回原文"，表现为目录/正文"看起来正常但不对"。</para>
    /// </summary>
    private static JsValue BuildBook(Engine engine, JsScope scope, JsErrorSink sink, Action<string> missing)
    {
        var book = scope.Book;
        var bookUrl = book?.BookUrl ?? string.Empty;
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("name", ToJs(engine, book?.Name ?? string.Empty));
        obj.FastSetDataProperty("author", ToJs(engine, book?.Author ?? string.Empty));
        obj.FastSetDataProperty("bookUrl", ToJs(engine, bookUrl));
        obj.FastSetDataProperty("tocUrl", ToJs(engine, book?.TocUrl ?? string.Empty));
        obj.FastSetDataProperty("intro", ToJs(engine, book?.Intro ?? string.Empty));
        obj.FastSetDataProperty("kind", ToJs(engine, book?.Kind ?? string.Empty));
        // type = 书籍类型（legado Book.type：0=文本）。**不是**书源类型
        // （scope.Source.BookSourceType：0/1/2/4）——旧实现张冠李戴，
        // 音视频书源(1/4)会被误报成"书籍类型 1/4"。JsBookState 暂无 Type
        // 字段，取不到时回落 0（文本），与 legado 对书源书籍的默认值一致。
        obj.FastSetDataProperty("type", ToJs(engine, book?.Type ?? 0));
        obj.FastSetDataProperty("coverUrl", ToJs(engine, book?.CoverUrl ?? string.Empty));
        obj.FastSetDataProperty("origin", ToJs(engine, scope.StorageKey));
        obj.FastSetDataProperty("durChapterIndex", ToJs(engine, book?.DurChapterIndex ?? 0));
        obj.FastSetDataProperty("durChapterTitle", ToJs(engine, book?.DurChapterTitle ?? string.Empty));
        obj.FastSetDataProperty("customIntro", ToJs(engine, book?.Intro ?? string.Empty));
        obj.FastSetDataProperty("totalChapterNum", ToJs(engine, book?.TotalChapterNum ?? -1));

        // readConfig 是活对象：setUseReplaceRule 会同时改这里（legado 语义：读到的就是刚设的）
        var readConfig = BuildReadConfig(engine, scope);
        obj.FastSetDataProperty("readConfig", readConfig);

        obj.FastSetDataProperty("getKey", CreateFunction(engine, "getKey", _ => bookUrl, sink));
        obj.FastSetDataProperty("getVariable", CreateFunction(engine, "getVariable",
            args => scope.Read(scope.BookKey(JsPureApi.AsString(args, 0))) ?? string.Empty, sink));
        var putVariable = CreateFunction(engine, "putVariable", args =>
        {
            WriteState(scope, sink, scope.BookKey(JsPureApi.AsString(args, 0)), JsPureApi.AsString(args, 1), "book.putVariable");
            return true;
        }, sink);
        obj.FastSetDataProperty("putVariable", putVariable);
        obj.FastSetDataProperty("setVariable", putVariable);
        obj.FastSetDataProperty("putCustomVariable", putVariable);

        // 阅读配置（legado Book.readConfig.useReplaceRule；真实落点是本地的"替换净化"开关）
        obj.FastSetDataProperty("getUseReplaceRule", CreateFunction(engine, "getUseReplaceRule",
            _ => (object?)(ReadUseReplaceRule(scope) ?? true), sink));
        obj.FastSetDataProperty("setUseReplaceRule", CreateFunction(engine, "setUseReplaceRule", args =>
        {
            var enabled = args.Length > 0 && args[0] is not (null or false);
            WriteState(scope, sink, scope.ReadConfigKey(), enabled ? "true" : "false", "book.setUseReplaceRule");
            readConfig.FastSetDataProperty("useReplaceRule", ToJs(engine, enabled));
            return null;
        }, sink));

        // 无宿主落点的成员：记录诊断（不假装成功）
        obj.FastSetDataProperty("setReverseToc", Stub(engine, missing, "book.setReverseToc", true, sink));

        return WrapWithMissingFallback(engine, obj, "book.", missing);
    }

    /// <summary><c>book.readConfig</c> 对象（只放有真实落点的字段：替换净化开关）。</summary>
    private static JsObject BuildReadConfig(Engine engine, JsScope scope)
    {
        var config = new JsObject(engine);
        config.FastSetDataProperty("useReplaceRule", ToJs(engine, ReadUseReplaceRule(scope) ?? true));
        return config;
    }

    /// <summary>读该书的替换净化开关（未设置返回 null = 跟随总开关）。</summary>
    private static bool? ReadUseReplaceRule(JsScope scope)
    {
        if (string.IsNullOrEmpty(scope.Book?.BookUrl))
        {
            return null;
        }

        return scope.Read(scope.ReadConfigKey()) switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    // ============================================================
    // cache
    // ============================================================

    /// <summary>
    /// <c>cache</c>：书源脚本的键值缓存。普通键落盘（<c>c:</c>），<c>*File</c> 用独立前缀（<c>file:</c>），
    /// <c>*Memory</c> 只在本进程内（<see cref="SourceStateStore"/> 的内存字典）。
    /// <para>对齐 reader：<c>get</c> 缺键返回 <c>null</c>（书源会用 <c>=== null</c> 判断），
    /// <c>put</c> 返回写入值，<c>getFromMemory</c> 能解析成 JSON 就返回对象、否则返回原串。</para>
    /// </summary>
    private static JsValue BuildCache(Engine engine, JsScope scope, JsErrorSink sink, Action<string> missing)
    {
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("get", CreateFunction(engine, "get",
            args => (object?)scope.Read(scope.CacheKey(JsPureApi.AsString(args, 0))), sink));
        obj.FastSetDataProperty("put", CreateFunction(engine, "put", args =>
        {
            var value = JsPureApi.AsString(args, 1);
            WriteState(scope, sink, scope.CacheKey(JsPureApi.AsString(args, 0)), value, "cache.put");
            return value;
        }, sink));
        obj.FastSetDataProperty("delete", CreateFunction(engine, "delete", args =>
        {
            scope.Delete(scope.CacheKey(JsPureApi.AsString(args, 0)));
            return null;
        }, sink));

        obj.FastSetDataProperty("getFile", CreateFunction(engine, "getFile",
            args => (object?)scope.Read(scope.FileCacheKey(JsPureApi.AsString(args, 0))), sink));
        obj.FastSetDataProperty("putFile", CreateFunction(engine, "putFile", args =>
        {
            var value = JsPureApi.AsString(args, 1);
            WriteState(scope, sink, scope.FileCacheKey(JsPureApi.AsString(args, 0)), value, "cache.putFile");
            return value;
        }, sink));
        obj.FastSetDataProperty("deleteFile", CreateFunction(engine, "deleteFile", args =>
        {
            scope.Delete(scope.FileCacheKey(JsPureApi.AsString(args, 0)));
            return null;
        }, sink));

        obj.FastSetDataProperty("getFromMemory", CreateFunction(engine, "getFromMemory", args =>
        {
            var raw = scope.Store.GetMemory(scope.StorageKey, JsPureApi.AsString(args, 0));
            if (raw is null)
            {
                return null;
            }

            // reader：能解析成 JSON 就返回对象（书源常直接读 .field），否则原样返回字符串
            try
            {
                return engine.Evaluate("JSON.parse(" + JsonSerializer.Serialize(raw) + ")");
            }
            catch
            {
                return raw;
            }
        }, sink));
        obj.FastSetDataProperty("putMemory", CreateFunction(engine, "putMemory", args =>
        {
            var key = JsPureApi.AsString(args, 0);
            var raw = args.Length > 1 ? JsonSerializer.Serialize(args[1]) : "null";
            scope.Store.SetMemory(scope.StorageKey, key, raw);
            return args.Length > 1 ? args[1] : null;
        }, sink));
        obj.FastSetDataProperty("deleteMemory", CreateFunction(engine, "deleteMemory", args =>
        {
            scope.Store.RemoveMemory(scope.StorageKey, JsPureApi.AsString(args, 0));
            return null;
        }, sink));

        return WrapWithMissingFallback(engine, obj, "cache.", missing);
    }

    // ============================================================
    // cookie
    // ============================================================

    /// <summary>
    /// <c>cookie</c>：Cookie 仓库的门面。阶段 4-3 起全部委托给 <see cref="ISourceCookieStore"/>：
    /// 与静态 HTTP 请求、<c>java.ajax</c> 共用同一份容器（每源一个 CookieContainer，
    /// 按近似二级域名归并 → 跨子域可读），脚本侧写法与 legado 一致。
    /// </summary>
    private static JsValue BuildCookie(Engine engine, JsScope scope, JsErrorSink sink, Action<string> missing)
    {
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("getCookie", CreateFunction(engine, "getCookie",
            args => JsCookieJar.GetCookie(scope, JsPureApi.AsString(args, 0)), sink));
        obj.FastSetDataProperty("getKey", CreateFunction(engine, "getKey",
            args => JsCookieJar.GetKey(scope, JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1)), sink));
        var setCookie = CreateFunction(engine, "setCookie", args =>
            JsCookieJar.SetCookie(scope, JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1)), sink);
        obj.FastSetDataProperty("setCookie", setCookie);
        obj.FastSetDataProperty("setWebCookie", setCookie);
        obj.FastSetDataProperty("replaceCookie", CreateFunction(engine, "replaceCookie", args =>
        {
            JsCookieJar.ReplaceCookie(scope, JsPureApi.AsString(args, 0), JsPureApi.AsString(args, 1));
            return null;
        }, sink));
        obj.FastSetDataProperty("removeCookie", CreateFunction(engine, "removeCookie",
            args => JsCookieJar.RemoveCookie(scope, JsPureApi.AsString(args, 0)), sink));

        return WrapWithMissingFallback(engine, obj, "cookie.", missing);
    }

    // ============================================================
    // html（记录型空桩，见类注释）
    // ============================================================

    private static JsValue BuildHtmlStub(Engine engine, Action<string> missing) =>
        WrapWithMissingFallback(engine, new JsObject(engine), "html.", missing);

    // ============================================================
    // java 的 put/get（AnalyzeRule 语义，JsBridge 调用）
    // ============================================================

    /// <summary>
    /// <c>java.get(key)</c>（= legado <c>AnalyzeRule.get</c>）：
    /// ① <c>bookName</c> → <c>book.name</c>；② <c>title</c> → 当前章名；
    /// ③ 章节变量 → 书籍变量 → 书源 <c>sp:</c> 键，逐级回退。
    /// <para>这个回退顺序是"目录链 put、正文链 get"能成立的关键：目录链没有章节，值落在书籍级；
    /// 正文链先查章节级（未命中）再查书籍级（命中）。</para>
    /// </summary>
    public static object? RuleGet(JsScope scope, string key)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (string.Equals(key, "bookName", StringComparison.Ordinal) && !string.IsNullOrEmpty(scope.Book?.BookUrl))
        {
            return scope.Book!.Name ?? string.Empty;
        }

        if (string.Equals(key, "title", StringComparison.Ordinal) && scope.ChapterUrl is not null)
        {
            return scope.Book?.DurChapterTitle ?? string.Empty;
        }

        if (scope.ChapterUrl is not null)
        {
            var chapterValue = scope.Read(scope.ChapterKey(key));
            if (chapterValue is not null)
            {
                return chapterValue;
            }
        }

        if (!string.IsNullOrEmpty(scope.Book?.BookUrl))
        {
            var bookValue = scope.Read(scope.BookKey(key));
            if (bookValue is not null)
            {
                return bookValue;
            }
        }

        return scope.Read(scope.SourcePutKey(key)) ?? string.Empty;
    }

    /// <summary>
    /// <c>java.put(key, value)</c>：有章节 → 章节变量；否则有书 → 书籍变量；否则书源 <c>sp:</c> 键。
    /// <para>返回值对齐 legado（<c>put</c> 返回写入值）。</para>
    /// </summary>
    public static object? RulePut(JsScope scope, string key, string value)
    {
        if (scope.ChapterUrl is not null)
        {
            scope.Write(scope.ChapterKey(key), value);
        }
        else if (!string.IsNullOrEmpty(scope.Book?.BookUrl))
        {
            scope.Write(scope.BookKey(key), value);
        }
        else
        {
            scope.Write(scope.SourcePutKey(key), value);
        }

        return value;
    }

    // ============================================================
    // JS 互操作小工具（JsBridge 共用，避免两份实现漂移）
    // ============================================================

    /// <summary>错误接收器：单个原语内部异常只记录 + 返回 null，绝不让整段脚本作废。</summary>
    internal readonly record struct JsErrorSink(JsDiagnostics? Diagnostics, string? SourceKey, string? Field)
    {
        public void Record(Exception exception, string name) =>
            Diagnostics?.RecordScriptError(SourceKey, Field, exception);

        /// <summary>状态写入被拒（值超长 / 键数超限 / 磁盘不可写）：脚本继续跑，但诊断里必须可见。</summary>
        public void RecordState(string name) =>
            Diagnostics?.RecordStateError(SourceKey, Field, name);
    }

    /// <summary>写状态并把"被拒绝"记进诊断（<see cref="SourceStateStore.Set"/> 返回 false 的三种原因）。</summary>
    private static void WriteState(JsScope scope, JsErrorSink sink, string key, string? value, string api)
    {
        if (!scope.Write(key, value))
        {
            sink.RecordState(api);
        }
    }

    /// <summary>
    /// 把一个 CLR 委托包成 JS 函数：参数统一转成 <c>object?[]</c>，
    /// 返回值再由 <see cref="ToJs"/> 转回 JS。<b>异常吞在函数内部</b>（记诊断 + 返回 null），
    /// 因为一个原语写错不该让整条解析链回退原文。
    /// </summary>
    internal static JsValue CreateFunction(
        Engine engine, string name, Func<object?[], object?> body, JsErrorSink sink)
    {
        return new ClrFunction(engine, name, (_, args) =>
        {
            var converted = new object?[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                converted[i] = args[i].ToObject();
            }

            object? result;
            try
            {
                result = body(converted);
            }
            catch (Exception ex)
            {
                sink.Record(ex, name);
                return JsValue.Null;
            }

            return ToJs(engine, result);
        });
    }

    /// <summary>CLR 值 → JS 值（数字统一走 double：Jint 的数值只有一种类型）。</summary>
    internal static JsValue ToJs(Engine engine, object? value) => value switch
    {
        null => JsValue.Null,
        JsValue js => js,
        string s => JsValue.FromObject(engine, s),
        bool b => JsValue.FromObject(engine, b),
        long l => JsValue.FromObject(engine, (double)l),
        int i => JsValue.FromObject(engine, (double)i),
        double d => JsValue.FromObject(engine, d),
        _ => JsValue.FromObject(engine, value),
    };

    /// <summary>
    /// 把一个对象包成"未知成员自动兜底"的 Proxy：<paramref name="onMissing"/> 记一条
    /// <see cref="JsDiagnosticKind.MissingApi"/>，脚本拿到 <c>null</c> 继续跑。
    /// <para><b>回调名用全局自增序号</b>：同一个引擎里会有 java/source/book/cache/cookie/html
    /// 六个代理，共用 <c>__legado_missing</c> 会互相覆盖（后建的把先建的顶掉，
    /// 诊断就会张冠李戴）。</para>
    /// </summary>
    internal static JsValue WrapWithMissingFallback(
        Engine engine, JsValue target, string prefix, Action<string> onMissing)
    {
        var seq = Interlocked.Increment(ref _proxySeq);
        var callbackName = "__legado_missing_" + seq;
        engine.SetValue(callbackName, new Action<string>(name => onMissing(prefix + name)));

        var targetName = "__legado_proxy_target_" + seq;
        engine.SetValue(targetName, target);
        engine.SetValue("__legado_proxy_cb", engine.GetValue(callbackName));

        // 刻意用字符串拼 JS（不用模板字符串），避免 C# 侧 {} 转义噪音。
        var proxy = engine.Evaluate(
            "(function(){var t=" + targetName + ",cb=__legado_proxy_cb;" +
            "if(typeof Proxy==='undefined')return t;" +
            "return new Proxy(t,{get:function(o,k){if(k in o)return o[k];" +
            "if(typeof k!=='string')return undefined;" +
            "return function(){cb(k);return null;};}});})()");

        engine.SetValue(targetName, JsValue.Undefined);
        engine.SetValue("__legado_proxy_cb", JsValue.Undefined);
        return proxy;
    }

    /// <summary>
    /// "记录一次 + 返回固定值"的空桩函数（宿主能力缺失时用，禁止假装成功）。
    /// <para><paramref name="member"/> 是<b>含对象前缀的完整成员名</b>（如 <c>source.refreshExplore</c>）：
    /// 这里直接用未加前缀的记录器，不能交给 <see cref="WrapWithMissingFallback"/> 的
    /// <c>onMissing</c>（那个会再补一次前缀，得到 <c>source.source.refreshExplore</c>）。</para>
    /// </summary>
    internal static JsValue Stub(
        Engine engine, Action<string> missing, string member, object? returnValue, JsErrorSink sink) =>
        CreateFunction(engine, member, _ =>
        {
            missing(member);
            return returnValue;
        }, sink);

    /// <summary>把参数拼成一行文本（诊断样本用；过长的由 <see cref="JsDiagnostics"/> 截断）。</summary>
    internal static string FormatArgs(object?[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(args[i] switch
            {
                null => "null",
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                _ => args[i]!.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>参数按"字符串或 JSON 文本"取（<c>putLoginInfo/putLoginHeader</c> 两种形态都收）。</summary>
    private static string StringifyArg(object?[] args, int index)
    {
        if (args.Length <= index || args[index] is null)
        {
            return string.Empty;
        }

        return args[index] switch
        {
            string s => s,
            _ => JsonSerializer.Serialize(args[index]),
        };
    }

    /// <summary>
    /// 把 JSON 文本解析成<b>带 Map 访问器</b>的 JS 对象（<c>get/put/has</c>）。
    /// <para>为什么不用 Jint 的 CLR 包装：<c>Dictionary</c> 在 JS 侧没有 <c>.get(k)</c> 方法，
    /// 而书源确实会写 <c>loginInfoMap.get('源作者')</c>。这里用 <c>JSON.parse</c> 拿到普通对象后
    /// 再用一段小脚本补三个方法，两种写法（下标 / Map）都成立。</para>
    /// </summary>
    internal static JsValue ParseJsonWithMapAccessors(Engine engine, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsObject(engine);
        }

        try
        {
            engine.SetValue("__legado_json_text", json);
            var value = engine.Evaluate(
                "(function(){var m;try{m=JSON.parse(__legado_json_text);}catch(e){m={};}" +
                "if(m===null||typeof m!=='object')m={};" +
                "if(typeof m.get!=='function'){m.get=function(k){return m[k];};}" +
                "if(typeof m.put!=='function'){m.put=function(k,v){m[k]=v;return v;};}" +
                "if(typeof m.has!=='function'){m.has=function(k){return k in m;};}" +
                "return m;})()");
            return value;
        }
        catch
        {
            return new JsObject(engine);
        }
        finally
        {
            // 异常路径也要清理：旧实现 catch 直接 return，__legado_json_text 全局
            // 残留在引擎上——下一次调用（可能已是另一个书源）的脚本能读到
            // 上一个书源的 loginInfo JSON（跨源串味）。
            engine.SetValue("__legado_json_text", JsValue.Undefined);
        }
    }
}
/// <summary>
/// 脚本侧 Cookie 门面（<c>cookie.*</c> 与 <c>java.getCookie</c> 共用）。
///
/// <para><b>阶段 4-3 起不再自带存储</b>：全部委托给注入的 <see cref="ISourceCookieStore"/>
/// （生产是 <see cref="SourceCookieStore"/>，按源一个 <see cref="System.Net.CookieContainer"/>，
/// 与静态 HTTP 请求、<c>java.ajax</c> 共用同一份）。这样"脚本 setCookie 的登录态"能被下一次
/// 普通取数立即带上，反之亦然 —— 这正是方案里 D3（会话割裂）要消灭的东西。</para>
///
/// <para>域归并（legado <c>getSubDomain</c> 语义）由 <see cref="SourceCookieStore.DomainOf"/> 负责：
/// <c>www.a.com</c> 写入的 Cookie 在 <c>m.a.com</c> 也读得到。</para>
/// </summary>
internal static class JsCookieJar
{
    /// <summary>取 <paramref name="url"/> 的 Cookie 串（不存在返回空串，对齐 legado）。</summary>
    public static string GetCookie(JsScope scope, string url) =>
        scope.Cookies.GetCookieHeader(scope.SourceKey, url);

    /// <summary>取 Cookie 串里某个键的值（legado <c>CookieStore.getKey</c>）；不存在返回空串。</summary>
    public static string GetKey(JsScope scope, string url, string key) =>
        scope.Cookies.GetCookieValue(scope.SourceKey, url, key);

    /// <summary>写 Cookie（覆盖该域）。返回是否写入成功。</summary>
    public static bool SetCookie(JsScope scope, string url, string? cookie) =>
        scope.Cookies.SetCookieHeader(scope.SourceKey, url, cookie ?? string.Empty);

    /// <summary>合并写 Cookie（同名键以新值为准，对齐 legado <c>replaceCookie</c>）。</summary>
    public static void ReplaceCookie(JsScope scope, string url, string? cookie)
    {
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(cookie))
        {
            scope.Cookies.ReplaceCookieHeader(scope.SourceKey, url, cookie);
        }
    }

    /// <summary>删 Cookie（返回是否真删掉了东西）。</summary>
    public static bool RemoveCookie(JsScope scope, string url) =>
        scope.Cookies.RemoveCookie(scope.SourceKey, url);

    /// <summary>
    /// 从登录请求头 JSON 里抽出 <c>Cookie</c> 并写入 Cookie 仓库
    /// （legado <c>putLoginHeader</c> 的行为：登录头里的 Cookie 必须能被普通取数与
    /// <c>cookie.getKey</c> 读到）。
    /// </summary>
    public static void MergeCookieHeader(JsScope scope, string sourceUrl, string? headerJson)
    {
        if (string.IsNullOrWhiteSpace(headerJson) || headerJson[0] != '{')
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(headerJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "Cookie", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var cookie = property.Value.GetString();
                    if (!string.IsNullOrEmpty(cookie))
                    {
                        ReplaceCookie(scope, sourceUrl, cookie);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 登录头不是合法 JSON：忽略（不能因为登录信息格式不对就中断脚本）
        }
    }
}

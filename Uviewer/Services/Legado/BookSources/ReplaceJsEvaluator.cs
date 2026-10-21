using System.Collections.Concurrent;
using Esprima.Ast;
using Jint;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 替换净化规则 <c>@js:</c> 形态的求值器（对标 legado <c>RegexExtensions.replace</c>）。
///
/// <para><b>语义（与 legado 逐行对齐）</b>：</para>
/// <list type="number">
/// <item>仅 <b>isRegex=true</b> 的规则评估 <c>@js:</c>（legado 字面分支走 Kotlin stdlib replace，不评估）；
/// 本类只被 ContentPurifier 的正则分支调用，字面分支天然不走这里。</item>
/// <item>去掉 <c>@js:</c> 前缀后，对正则的<b>每一个匹配</b>求值一次，
/// 绑定 <c>result</c> = 该匹配串（legado: <c>bindings["result"] = matcher.group()</c>）。</item>
/// <item>脚本的 completion 值（语句型 <c>if(…){…}else{…}</c> 取分支值，表达式型取表达式值）
/// 经 ToString 后作为该匹配的替换内容；legado 侧为 Rhino <c>eval(...).toString()</c>，
/// Jint 的 <see cref="Engine.Evaluate(Prepared{Script})"/> 同样返回 completion 值（探针实证）。</item>
/// <item>求值异常（语法错误 / 运行期错误）向上抛，由 ContentPurifier 的逐规则 catch 兜底
/// ⇒ <b>整条规则跳过</b>、文本不被部分改写（legado: 异常使整个 replace 调用失败，
/// mContent 保持原值；reader: try/catch 包住整条规则）。</item>
/// </list>
///
/// <para><b>与 legado 的有意偏差</b>：legado 还绑定 <c>chapter</c> / <c>book</c> 两个对象，
/// 本环境（正文净化管线）没有这两个概念的对应物，故不绑定；引用了它们的脚本会抛
/// ReferenceError ⇒ 规则跳过（宁可明确不生效，也不凭空造语义）。</para>
///
/// <para><b>性能设计（为什么这样写）</b>：</para>
/// <list type="bullet">
/// <item><b>编译缓存</b>：<see cref="Engine.PrepareScript"/> 是静态方法，解析产物（AST）不可变，
/// 按脚本文本缓存——同一规则的脚本只解析一次（4KB 脚本约 5ms，逐匹配解析完全不可接受）。</item>
/// <item><b>引擎池</b>：Jint 引擎构造昂贵（~0.05ms 且分配大）且非线程安全。
/// 净化在窗口预取（并发 4）/ 整本下载（并发 8）下并行发生，池按并发租借；
/// 池满则临时新建（用即弃，不进池）。</item>
/// <item><b>无循环看门狗</b>：Jint 3 无语句数/超时 API（2.x 的 MaxStatements 已移除），
/// 与项目现状一致——书源规则引擎（<c>RuleEngine</c>）同样以默认选项执行任意 JS。
/// legado 的 3s 看门狗 + 重启应用在本桌面端没有对应物，也不打算照搬（那是手机端的止血方案）。</item>
/// </list>
/// </summary>
internal static class ReplaceJsEvaluator
{
    /// <summary>引擎池上限（≥ 最大净化并发：窗口预取 4 / 整本下载 8）。</summary>
    private const int EnginePoolCap = 8;

    private static readonly ConcurrentBag<Engine> Engines = new();

    /// <summary>替换串是否为 <c>@js:</c> 形态（大小写敏感，与 legado <c>startsWith("@js:")</c> 一致）。</summary>
    public static bool IsJs(string replacement) =>
        replacement.StartsWith("@js:", StringComparison.Ordinal);

    /// <summary>去掉 <c>@js:</c> 前缀的脚本文本（调用前须已 <see cref="IsJs"/> 判定）。</summary>
    public static string ScriptOf(string replacement) => replacement.Substring(4);

    /// <summary>
    /// 对单个匹配求值。返回替换文本（JS null/undefined 也按字符串 "null"/"undefined" 处理，同 Rhino）。
    /// </summary>
    /// <exception cref="Exception">Jint 解析/运行期异常原样上抛（调用方按"整条规则跳过"处理）。</exception>
    public static string Evaluate(string script, string matched)
    {
        var engine = Rent();
        try
        {
            // result = 当前匹配串（legado bindings["result"] = matcher.group()）
            engine.SetValue("result", matched);
            return engine.Evaluate(GetPrepared(script)).ToString();
        }
        finally
        {
            Return(engine);
        }
    }

    /// <summary>
    /// 取编译产物（统一走 <see cref="JsScriptCache"/>：与 <see cref="JsBridge"/> 共用同一份 AST 缓存，
    /// 不再各自维护一份）。
    /// </summary>
    private static Prepared<Script> GetPrepared(string script) => JsScriptCache.Get(script);

    /// <summary>租借引擎：池空则新建（默认选项，与 <c>RuleEngine</c> 的 JS 执行策略一致）。</summary>
    private static Engine Rent() => Engines.TryTake(out var engine)
        ? engine
        : new Engine();

    /// <summary>归还引擎：池满则丢弃（GC 回收），避免池无限增长。</summary>
    private static void Return(Engine engine)
    {
        if (Engines.Count < EnginePoolCap)
        {
            Engines.Add(engine);
        }
    }
}

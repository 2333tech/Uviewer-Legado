using System.Collections.Concurrent;
using Esprima.Ast;
using Jint;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// Jint 脚本<b>编译产物（AST）缓存</b>：<c>Engine.PrepareScript</c> 的解析开销随脚本长度上升
/// （4KB 脚本约 5ms），而书源脚本在"每章 × 每条规则"上反复执行——逐次解析完全不可接受。
///
/// <para><b>为什么可以跨引擎/跨线程共享</b>：<see cref="Prepared{T}"/> 是不可变的语法树，
/// <see cref="Engine"/> 只是它的执行者。同一个 prepared 可以喂给任意引擎（包括并行的池化引擎）。</para>
///
/// <para><b>唯一实现</b>：正文净化的 <c>@js:</c> 求值（<see cref="ReplaceJsEvaluator"/>）与
/// 规则引擎的脚本段（<see cref="JsBridge"/>）都走这里，避免两处各写一份缓存
/// （早期只有净化侧做了缓存，规则引擎每章仍重复解析脚本）。</para>
///
/// <para><b>有界</b>：上限 <see cref="Capacity"/>，满后只编译不缓存（功能不受影响，下次重新解析），
/// 防止用户反复导入不同书源把进程内存顶上去。</para>
/// </summary>
internal static class JsScriptCache
{
    /// <summary>缓存条数上限（书源脚本数量级为百，512 足够）。</summary>
    private const int Capacity = 512;

    private static readonly ConcurrentDictionary<string, Prepared<Script>> Cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 取（必要时编译并缓存）脚本。语法错误按 <see cref="Exception"/> 原样上抛——
    /// 调用方（<see cref="JsBridge.Run"/> / <see cref="ContentPurifier"/>）都有"回退原文 / 跳过整条规则"的兜底。
    /// </summary>
    public static Prepared<Script> Get(string script)
    {
        if (Cache.TryGetValue(script, out var cached))
        {
            return cached;
        }

        // 并发下可能重复编译同一脚本：结果等价，后者覆盖前者，无害
        var prepared = Engine.PrepareScript(script);
        if (Cache.Count < Capacity)
        {
            Cache[script] = prepared;
        }

        return prepared;
    }
}

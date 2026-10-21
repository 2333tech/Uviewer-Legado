using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>诊断类别。</summary>
public enum JsDiagnosticKind
{
    /// <summary>脚本调用了一个不存在/未实现的原语（Proxy 兜底命中）。</summary>
    MissingApi,

    /// <summary>脚本调用了存在但只返回空值的桩（结果会静默失真）。</summary>
    StubCalled,

    /// <summary>脚本抛异常，已按 legado 语义回退原始内容。</summary>
    ScriptError,

    /// <summary><c>java.log</c> 输出。</summary>
    Log,

    /// <summary><c>java.toast</c> / <c>java.longToast</c> 输出。</summary>
    Toast,

    /// <summary>状态仓库读写失败/被拒（阶段 4-2：值超长、键数超限、磁盘不可写）。</summary>
    StateError,
}

/// <summary>一条诊断（同源/同字段/同类/同细节会聚合成 <see cref="Count"/> 次）。</summary>
public sealed record JsDiagnosticEntry(
    string SourceKey,
    string Field,
    JsDiagnosticKind Kind,
    string Detail,
    int Count,
    string Sample);

/// <summary>
/// 书源 JS 运行诊断（阶段 4-1 新增）。
/// <para><b>为什么需要它</b>：<see cref="JsBridge.Run"/> 在脚本异常时按 legado 语义"回退原文"，
/// 调用方拿到的是一份"看起来正常但其实没跑过脚本"的内容——此前这个过程完全不可见，
/// 用户只看到"目录不对/正文不对"。本类把三类静默事件变成可查询数据：</para>
/// <list type="number">
/// <item><see cref="JsDiagnosticKind.MissingApi"/>：脚本真正调用了未实现的原语（= 该源缺什么能力）；</item>
/// <item><see cref="JsDiagnosticKind.StubCalled"/>：调用了只返回空值的桩（= 结果为什么会空）；</item>
/// <item><see cref="JsDiagnosticKind.ScriptError"/>：脚本抛错（= 整段被回退）。</item>
/// </list>
/// <para>线程安全：解析链在多个线程池线程上并发跑，全部走并发字典 + Interlocked 计数。
/// 容量有界（<see cref="MaxEntries"/>），溢出不抛异常——诊断本身绝不能影响解析。</para>
/// </summary>
public sealed class JsDiagnostics
{
    /// <summary>条目上限（源 × 字段 × 类别 × 细节 的组合通常远小于此）。</summary>
    public const int MaxEntries = 1024;

    /// <summary>样本文本截断长度（避免把整章正文塞进诊断）。</summary>
    public const int MaxSampleLength = 200;

    /// <summary>进程级默认实例（解析链未显式注入时使用，界面/测试可直接读）。</summary>
    public static JsDiagnostics Shared { get; } = new();

    private readonly ConcurrentDictionary<Key, Counter> _entries = new();

    /// <summary>当前条目数（聚合计入，不随次数增长）。</summary>
    public int EntryCount => _entries.Count;

    /// <summary>记录"调用了未实现的原语"。</summary>
    public void RecordMissingApi(string? sourceKey, string? field, string api) =>
        Record(sourceKey, field, JsDiagnosticKind.MissingApi, api, null);

    /// <summary>记录"调用了返回空值的桩"。</summary>
    public void RecordStubCalled(string? sourceKey, string? field, string api, string? sample = null) =>
        Record(sourceKey, field, JsDiagnosticKind.StubCalled, api, sample);

    /// <summary>记录脚本异常（消息截断，避免堆栈把内存顶满）。</summary>
    public void RecordScriptError(string? sourceKey, string? field, Exception exception) =>
        Record(sourceKey, field, JsDiagnosticKind.ScriptError,
            exception.GetType().Name, exception.Message);

    /// <summary>记录 <c>java.log</c> 输出。</summary>
    public void RecordLog(string? sourceKey, string? field, string message) =>
        Record(sourceKey, field, JsDiagnosticKind.Log, "log", message);

    /// <summary>记录 <c>java.toast</c> / <c>java.longToast</c> 输出。</summary>
    public void RecordToast(string? sourceKey, string? field, string message) =>
        Record(sourceKey, field, JsDiagnosticKind.Toast, "toast", message);

    /// <summary>
    /// 记录状态仓库的读写失败（阶段 4-2）。
    /// <para>状态读写失败**不中断解析**（书源脚本拿到的仍是可用值/空值），但必须可见：
    /// "为什么登录态没记住 / 为什么变量跨链读不到"直接可查。</para>
    /// </summary>
    public void RecordStateError(string? sourceKey, string? field, string detail, string? sample = null) =>
        Record(sourceKey, field, JsDiagnosticKind.StateError, detail, sample);

    /// <summary>取快照（按次数降序；界面/测试用，调用频率低）。</summary>
    public IReadOnlyList<JsDiagnosticEntry> Snapshot()
    {
        var list = new List<JsDiagnosticEntry>(_entries.Count);
        foreach (var pair in _entries)
        {
            var counter = pair.Value;
            list.Add(new JsDiagnosticEntry(
                pair.Key.SourceKey,
                pair.Key.Field,
                pair.Key.Kind,
                pair.Key.Detail,
                Volatile.Read(ref counter.Count),
                Volatile.Read(ref counter.Sample) ?? string.Empty));
        }

        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    /// <summary>某书源调用过的未实现原语名（去重，升序）。</summary>
    public IReadOnlyList<string> MissingApisOf(string sourceKey)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pair in _entries)
        {
            if (pair.Key.Kind == JsDiagnosticKind.MissingApi
                && string.Equals(pair.Key.SourceKey, sourceKey, StringComparison.Ordinal))
            {
                set.Add(pair.Key.Detail);
            }
        }

        return new List<string>(set);
    }

    /// <summary>清空（测试与"重新导入书源"后调用）。</summary>
    public void Clear() => _entries.Clear();

    private void Record(string? sourceKey, string? field, JsDiagnosticKind kind, string detail, string? sample)
    {
        var key = new Key(
            sourceKey ?? string.Empty,
            field ?? string.Empty,
            kind,
            Truncate(detail, MaxSampleLength) ?? string.Empty);

        if (_entries.TryGetValue(key, out var existing))
        {
            Interlocked.Increment(ref existing.Count);
            if (existing.Sample is null && !string.IsNullOrEmpty(sample))
            {
                Volatile.Write(ref existing.Sample, Truncate(sample, MaxSampleLength));
            }

            return;
        }

        // 容量熔断：满了只累计已有条目，不再新增（诊断绝不能反噬解析）
        if (_entries.Count >= MaxEntries)
        {
            return;
        }

        var counter = new Counter { Sample = Truncate(sample, MaxSampleLength) };
        counter.Count = 1;
        _entries.TryAdd(key, counter);
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text.Substring(0, max) + "…";
    }

    private readonly record struct Key(string SourceKey, string Field, JsDiagnosticKind Kind, string Detail);

    private sealed class Counter
    {
        public int Count;
        public string? Sample;
    }
}

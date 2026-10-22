using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Uviewer.Services.Legado.Models;

/// <summary>
/// System.Text.Json 源生成上下文（AOT 友好：编译期生成序列化代码，零反射）。
/// <para>全局策略：</para>
/// <list type="bullet">
/// <item>CamelCase：属性 PascalCase 输出为 Legado 标准 camelCase 键（bookSourceUrl / ruleSearch 等）。</item>
/// <item>WhenWritingNull：与 reader serde(default) 一致，null 字段省略不写
/// （旧字段别名依赖此策略：迁移后置 null 即永不回写）。</item>
/// <item>WriteIndented：落盘文件带缩进，便于人工 diff 书源文件。</item>
/// </list>
/// <para>新增需要序列化的类型时必须在此登记 [JsonSerializable]，否则运行期回退反射（本库零依赖约束下不可接受）。</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(BookSource))]
[JsonSerializable(typeof(List<BookSource>))]
[JsonSerializable(typeof(BookSource[]))]
[JsonSerializable(typeof(SearchRule))]
[JsonSerializable(typeof(ExploreRule))]
[JsonSerializable(typeof(BookInfoRule))]
[JsonSerializable(typeof(TocRule))]
[JsonSerializable(typeof(ContentRule))]
[JsonSerializable(typeof(ReviewRule))]
[JsonSerializable(typeof(ReplaceRule))]
[JsonSerializable(typeof(List<ReplaceRule>))]
[JsonSerializable(typeof(ReplaceRule[]))]
[JsonSerializable(typeof(TitleRule))]
[JsonSerializable(typeof(List<TitleRule>))]
[JsonSerializable(typeof(TitleRule[]))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(BookInfo))]
[JsonSerializable(typeof(TocItem))]
[JsonSerializable(typeof(List<TocItem>))]
[JsonSerializable(typeof(ChapterContent))]
[JsonSerializable(typeof(ReadingPosition))]
[JsonSerializable(typeof(List<ReadingPosition>))]
[JsonSerializable(typeof(ReadingPosition[]))]
// 漫画落盘（书级清单 / 章级配置）：与书源存储同一套 JSON 口径，便于外部工具读取。
[JsonSerializable(typeof(MangaManifest))]
[JsonSerializable(typeof(MangaChapterImages))]
public partial class BookSourceJsonContext : JsonSerializerContext;

/// <summary>序列化辅助：统一使用源生成上下文，零反射、零运行时编码配置。</summary>
public static class BookSourceSerializer
{
    /// <summary>反序列化单个书源对象。</summary>
    public static BookSource? DeserializeBookSource(string json) =>
        JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.BookSource);

    /// <summary>反序列化书源数组；输入为 null 字面量时返回空列表（调用方免判空）。</summary>
    public static List<BookSource> DeserializeBookSourceList(string json) =>
        JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.ListBookSource) ?? new();

    /// <summary>直接从流反序列化（不经字符串，省一次完整副本）。</summary>
    public static async Task<List<BookSource>> DeserializeBookSourceListAsync(
        Stream stream, CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync(
            stream, BookSourceJsonContext.Default.ListBookSource, cancellationToken).ConfigureAwait(false) ?? new();

    /// <summary>
    /// 流式枚举顶层数组元素（STJ DeserializeAsyncEnumerable）：
    /// 4.5MB 级书源文件只按元素逐个反序列化，峰值内存≈单条书源，而不是整份 DOM。
    /// 调用方可用 <c>Take(n)</c> 只取部分内容（测试的 SampleData 即此用法）。
    /// <para>每个元素 yield 前都过一遍 <see cref="LegacyAlias.Migrate"/>，保证旧键名书源开箱即用。</para>
    /// </summary>
    public static async IAsyncEnumerable<BookSource> EnumerateBookSourcesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var source in JsonSerializer.DeserializeAsyncEnumerable(
            stream, BookSourceJsonContext.Default.BookSource, cancellationToken).ConfigureAwait(false))
        {
            if (source is null)
            {
                // 数组里的 null 元素：跳过（不产生条目）
                continue;
            }

            LegacyAlias.Migrate(source);
            yield return source;
        }
    }

    /// <summary>反序列化单条全局替换规则。</summary>
    public static ReplaceRule? DeserializeReplaceRule(string json) =>
        JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.ReplaceRule);

    /// <summary>反序列化替换规则数组；输入为 null 字面量时返回空列表。</summary>
    public static List<ReplaceRule> DeserializeReplaceRuleList(string json) =>
        JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.ListReplaceRule) ?? new();

    /// <summary>
    /// 流式枚举替换规则数组元素（同 <see cref="EnumerateBookSourcesAsync"/> 的零 DOM 策略：
    /// 逐元素反序列化，峰值内存≈单条规则）。
    /// </summary>
    public static async IAsyncEnumerable<ReplaceRule> DeserializeReplaceRuleEnumerableAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var rule in JsonSerializer.DeserializeAsyncEnumerable(
            stream, BookSourceJsonContext.Default.ReplaceRule, cancellationToken).ConfigureAwait(false))
        {
            if (rule is null)
            {
                continue;
            }

            yield return rule;
        }
    }

    /// <summary>替换规则列表直接写入流（不经中间字符串）。</summary>
    public static Task SerializeReplaceRuleListAsync(
        Stream stream, IEnumerable<ReplaceRule> rules, CancellationToken cancellationToken = default)
    {
        var array = rules as ReplaceRule[] ?? rules.ToArray();
        return JsonSerializer.SerializeAsync(
            stream, array, BookSourceJsonContext.Default.ReplaceRuleArray, cancellationToken);
    }

    /// <summary>反序列化标题/章节划分规则数组（legado 旧键名自动迁移，见 <see cref="LegacyAlias.Migrate(TitleRule)"/>）。</summary>
    public static List<TitleRule> DeserializeTitleRuleList(string json)
    {
        var list = JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.ListTitleRule) ?? new();
        foreach (var rule in list)
        {
            LegacyAlias.Migrate(rule);
        }

        return list;
    }

    /// <summary>
    /// 流式枚举标题规则数组元素（零 DOM，峰值内存≈单条规则）。
    /// <para>每个元素 yield 前都过一遍 <see cref="LegacyAlias.Migrate(TitleRule)"/>，
    /// 保证 legado 的 TXTTopicRule.json（<c>rule</c>/<c>enable</c>/<c>serialNumber</c> 键名）开箱即用。</para>
    /// </summary>
    public static async IAsyncEnumerable<TitleRule> DeserializeTitleRuleEnumerableAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var rule in JsonSerializer.DeserializeAsyncEnumerable(
            stream, BookSourceJsonContext.Default.TitleRule, cancellationToken).ConfigureAwait(false))
        {
            if (rule is not null)
            {
                LegacyAlias.Migrate(rule);
                yield return rule;
            }
        }
    }

    /// <summary>标题规则列表直接写入流（不经中间字符串）。</summary>
    public static Task SerializeTitleRuleListAsync(
        Stream stream, IEnumerable<TitleRule> rules, CancellationToken cancellationToken = default)
    {
        var array = rules as TitleRule[] ?? rules.ToArray();
        return JsonSerializer.SerializeAsync(
            stream, array, BookSourceJsonContext.Default.TitleRuleArray, cancellationToken);
    }

    /// <summary>序列化单个书源。</summary>
    public static string Serialize(BookSource source) =>
        JsonSerializer.Serialize(source, BookSourceJsonContext.Default.BookSource);

    /// <summary>序列化书源列表为 JSON 数组文本。</summary>
    public static string SerializeList(IEnumerable<BookSource> sources) =>
        JsonSerializer.Serialize(sources.ToList(), BookSourceJsonContext.Default.ListBookSource);

    /// <summary>反序列化阅读进度数组；输入为 null 字面量时返回空列表。</summary>
    public static List<ReadingPosition> DeserializeReadingPositionList(string json) =>
        JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.ListReadingPosition) ?? new();

    /// <summary>从流反序列化阅读进度数组（不经字符串，省一次完整副本）。</summary>
    public static async Task<List<ReadingPosition>> DeserializeReadingPositionListAsync(
        Stream stream, CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync(
            stream, BookSourceJsonContext.Default.ListReadingPosition, cancellationToken).ConfigureAwait(false) ?? new();

    /// <summary>
    /// 阅读进度列表直接写入流（不经中间字符串）。
    /// <para>输入先物化为数组：序列化期间集合被并发修改会产生不一致输出，
    /// 且 BookSourceReadingStore 传入的本来就是不可变快照。</para>
    /// </summary>
    public static Task SerializeReadingPositionListAsync(
        Stream stream, IEnumerable<ReadingPosition> positions, CancellationToken cancellationToken = default)
    {
        var array = positions as ReadingPosition[] ?? positions.ToArray();
        return JsonSerializer.SerializeAsync(
            stream, array, BookSourceJsonContext.Default.ReadingPositionArray, cancellationToken);
    }

    /// <summary>
    /// 直接写入流（不经中间字符串，省掉整份 JSON 的一次完整内存副本）。
    /// <para>输入先物化为数组：序列化期间集合被并发修改会产生不一致输出，
    /// 且 BookSourceManager 传入的本来就是不可变快照。</para>
    /// </summary>
    public static Task SerializeListAsync(
        Stream stream, IEnumerable<BookSource> sources, CancellationToken cancellationToken = default)
    {
        var array = sources as BookSource[] ?? sources.ToArray();
        return JsonSerializer.SerializeAsync(
            stream, array, BookSourceJsonContext.Default.BookSourceArray, cancellationToken);
    }
}

/// <summary>
/// Legado 旧键名迁移（对齐 reader migrate_legacy_book_source_value 的 move_if_absent 语义）。
/// <para>旧键由 <see cref="BookSource"/> 上的 5 个别名属性（[JsonPropertyName] 直接接收）进入对象，
/// 这里做"目标字段缺失才补齐"（??=），补齐后把别名置 null —— 配合 WhenWritingNull 策略，
/// 旧键永不回写、且全程不需要 JsonNode DOM（这是阶段 0 性能架构的一环，勿退回 DOM 方案）。</para>
/// <para>5 个字段的迁移逻辑完全同构，看一个即可：</para>
/// </summary>
internal static class LegacyAlias
{
    /// <summary>
    /// 执行迁移。对每条旧字段：
    /// ① 旧键有值（not null）且目标字段无值 → 用 ??= 补齐目标字段；
    /// ② 无论是否补齐，旧键一律置 null（防止回写）。
    /// </summary>
    public static void Migrate(BookSource source)
    {
        // ① enable → Enabled（bool? → bool?）
        if (source.LegacyEnabled is not null)
        {
            source.Enabled ??= source.LegacyEnabled;
            source.LegacyEnabled = null;
        }

        // ② serialNumber → CustomOrder（int? → int?）
        if (source.LegacyCustomOrder is not null)
        {
            source.CustomOrder ??= source.LegacyCustomOrder;
            source.LegacyCustomOrder = null;
        }

        // ③ ruleSearchUrl → SearchUrl
        if (source.LegacySearchUrl is not null)
        {
            source.SearchUrl ??= source.LegacySearchUrl;
            source.LegacySearchUrl = null;
        }

        // ④ ruleFindUrl → ExploreUrl
        if (source.LegacyExploreUrl is not null)
        {
            source.ExploreUrl ??= source.LegacyExploreUrl;
            source.LegacyExploreUrl = null;
        }

        // ⑤ ruleBookUrlPattern → BookUrlPattern
        if (source.LegacyBookUrlPattern is not null)
        {
            source.BookUrlPattern ??= source.LegacyBookUrlPattern;
            source.LegacyBookUrlPattern = null;
        }
    }

    /// <summary>
    /// 标题规则的 legado 旧键名迁移（对标 <see cref="Migrate(BookSource)"/> 的 move_if_absent 语义）。
    /// <para>legado 的 TXT 分章规则文件（酷安流派的 TXTTopicRule.json）用
    /// <c>rule</c>（正则）/ <c>enable</c>（启用）/ <c>serialNumber</c>（排序）三个键名，
    /// 与本模型的 <see cref="TitleRule.Pattern"/>/<see cref="TitleRule.IsEnabled"/>/
    /// <see cref="TitleRule.Order"/> 一一对应；不迁移则整份文件解析成"空正则 + 停用"的垃圾数据
    /// （导入被静默跳过），用户完全看不出所以然。</para>
    /// <para>迁移后别名一律置 null —— 配合 WhenWritingNull 策略，回写时只输出本模型键名，
    /// 不会把 legado 键名带到 replaceRules/titleRules 存储文件里。</para>
    /// </summary>
    public static void Migrate(TitleRule rule)
    {
        // rule → Pattern。⚠ 不能用 ??= ：Pattern 的默认值是 string.Empty（非 null），
        // ??= 对空串是 no-op，会把 legado 的正则静默丢掉（导入后全是空 pattern 被跳过）。
        // 必须"空才补齐"（IsNullOrEmpty）。
        if (rule.LegacyRule is not null)
        {
            if (string.IsNullOrEmpty(rule.Pattern))
            {
                rule.Pattern = rule.LegacyRule;
            }

            rule.LegacyRule = null;
        }

        // enable → IsEnabled（bool 无"缺失"概念，legado 显式给了值就以它为准）
        if (rule.LegacyEnable is not null)
        {
            rule.IsEnabled = rule.LegacyEnable.Value;
            rule.LegacyEnable = null;
        }

        // serialNumber → Order（同上；legado 用负数表达"排在默认规则前面"）
        if (rule.LegacySerialNumber is not null)
        {
            rule.Order = rule.LegacySerialNumber.Value;
            rule.LegacySerialNumber = null;
        }
    }
}

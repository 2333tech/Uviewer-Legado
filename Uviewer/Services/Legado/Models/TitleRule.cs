using System.Text.Json.Serialization;

namespace Uviewer.Services.Legado.Models;

/// <summary>
/// 标题 / 章节划分规则（用户级，独立于 <see cref="BookSource"/>）：
/// 告诉阅读器"TXT 里哪一行算章节标题"，从而切分章节、生成目录、供顶栏目录按钮跳转。
///
/// <para><b>与 <see cref="ReplaceRule"/> 的分工</b>：
/// 替换规则管"正文怎么洗"（去广告、纠错字），本规则管"文本怎么分章"。
/// 两者都作用在正文管线，但一个改内容、一个改结构，互不干扰。</para>
///
/// <para><b>字段语义</b>：
/// <list type="bullet">
/// <item><see cref="Pattern"/>：识别章节标题行的<b>正则</b>（整行匹配，非搜索）。
/// 留空表示书名行（见 <see cref="IsBookTitle"/>）。</item>
/// <item><see cref="TitleGroup"/>：用正则的第几个捕获组作为目录里显示的章名；
/// 0 或未填 = 整行。空捕获组时回退到整行（避免目录出现空条目）。</item>
/// <item><see cref="IsVolume"/>：标记为"卷"（如"第一卷 风云"）——目录里可展示但不直接跳章。</item>
/// <item><see cref="IsBookTitle"/>：标记为书名行（只出现在正文开头，用于提取书名）。</item>
/// <item><see cref="Level"/>：目录层级，1 = 章，2 = 节（更细）。</item>
/// </list></para>
///
/// <para><b>执行顺序</b>：按 <see cref="Order"/> 升序（同值保持导入顺序），
/// 第一条命中的规则生效——与替换规则的顺序语义一致。</para>
/// </summary>
public sealed class TitleRule
{
    /// <summary>主键（0 或缺失时由存储层分配 max(Id)+1）。</summary>
    public long Id { get; set; }

    /// <summary>规则名（UI 展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组（可选，仅 UI 归类）。</summary>
    public string? Group { get; set; }

    /// <summary>章节标题行的正则（整行匹配）。空 = 无效规则（跳过）。</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>取第几个捕获组作为章名（0/未填 = 整行）。</summary>
    public int TitleGroup { get; set; }

    /// <summary>是否为"卷"标题（目录展示但不跳章）。</summary>
    [JsonPropertyName("isVolume")]
    public bool IsVolume { get; set; }

    /// <summary>是否为书名行（用于提取书名，不计入章节）。</summary>
    [JsonPropertyName("isBookTitle")]
    public bool IsBookTitle { get; set; }

    /// <summary>目录层级（1 = 章，2 = 节）。</summary>
    public int Level { get; set; } = 1;

    /// <summary>是否启用（旧键名 isEnabled）。</summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>执行顺序（升序）。</summary>
    public int Order { get; set; }

    // ---- legado 旧键名别名（导入 legado/reader 的 TXTTopicRule.json 时映射，不回写） ----

    /// <summary>legado 旧键名 <c>rule</c>（正则）。由 <see cref="LegacyAlias.Migrate"/> 迁到 <see cref="Pattern"/>。</summary>
    [JsonPropertyName("rule")]
    public string? LegacyRule { get; set; }

    /// <summary>legado 旧键名 <c>enable</c>（启用）。迁到 <see cref="IsEnabled"/>。</summary>
    [JsonPropertyName("enable")]
    public bool? LegacyEnable { get; set; }

    /// <summary>legado 旧键名 <c>serialNumber</c>（排序）。迁到 <see cref="Order"/>。</summary>
    [JsonPropertyName("serialNumber")]
    public int? LegacySerialNumber { get; set; }
}

/// <summary>一次导入的结果统计（净化规则 / 标题规则共用）。</summary>
/// <param name="Added">新增条数。</param>
/// <param name="Updated">覆盖已有条数（按主键）。</param>
/// <param name="Skipped">跳过条数（无效 / 无名称）。</param>
public readonly record struct RuleImportResult(int Added, int Updated, int Skipped)
{
    /// <summary>总处理条数。</summary>
    public int Total => Added + Updated + Skipped;
}

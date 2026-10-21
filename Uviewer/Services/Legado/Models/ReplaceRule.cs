using System.Text.Json.Serialization;

namespace Uviewer.Services.Legado.Models;

/// <summary>
/// 全局替换规则（独立于 BookSource，用户级：对章节正文做统一净化）。
/// 字段对齐 reader/src/model/replace_rule.rs 与 legado ReplaceRule.kt（两端共有字段）。
/// <para>执行模型（对标 reader frontend stores/reader.ts 的 applyReplaceRules，
/// 应用管线见 <see cref="Uviewer.Services.Legado.BookSources.ContentPurifier"/>）：</para>
/// <list type="number">
/// <item>按 <see cref="Order"/> 升序执行（稳定排序：同 order 保持存储顺序）；</item>
/// <item><see cref="Scope"/> 为空或 "*" = 全部书籍；其余形态：
/// "source:&lt;书源URL&gt;" / "book:&lt;书籍URL&gt;" / "&lt;书名&gt;[;&lt;书籍URL&gt;]"
/// （详见 ContentPurifier.ScopeMatches，与 reader matchRuleScope 逐分支一致）；</item>
/// <item><see cref="IsRegex"/> 决定正则（global+multiline）还是字面替换。</item>
/// </list>
/// <para>注意：reader 前端只把替换规则应用于**章节正文**（不净化书名/搜索结果），
/// 故本库同样只在正文管线接线。UI 新建规则的默认值（scope='*' / isEnabled=true /
/// isRegex=false / order=长度+1）与本模型默认值一致。</para>
/// </summary>
public sealed class ReplaceRule
{
    /// <summary>主键（0 或缺失时由存储层分配 max(Id)+1，避免碰撞）。</summary>
    public long Id { get; set; }

    /// <summary>规则名（UI 展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组（可选，仅 UI 归类用，不参与匹配）。</summary>
    public string? Group { get; set; }

    /// <summary>匹配模式（正则或字面，依 <see cref="IsRegex"/>）。空 pattern 的规则被跳过（legado 语义）。</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>替换内容（正则形态支持 $1 / $&amp; 等分组引用，与 JS RegExp.replace 语法一致）。</summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>作用范围：空/'*' 全部；source:&lt;origin&gt;；book:&lt;bookUrl&gt;；&lt;name&gt;[;&lt;bookUrl&gt;]（见 ContentPurifier.ScopeMatches）。</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// 是否作用于<b>标题</b>（legado <c>scopeTitle</c>，默认 false）。
    /// <para>作用于标题的规则会在"标题/分章规则"识别章节行<b>之前</b>先跑一遍目标行，
    /// 这样"第123章 书名"这类需要先清理再识别的标题才能被标题规则匹配到。</para>
    /// </summary>
    [JsonPropertyName("scopeTitle")]
    // EN: legado scopeTitle - rule also applies to heading lines before title rules run.
    public bool ScopeTitle { get; set; }

    /// <summary>
    /// 是否作用于<b>正文</b>（legado <c>scopeContent</c>，默认 true）。
    /// <para>与 <see cref="ScopeTitle"/> 互相独立：可以为只洗标题、只洗正文、或两者都洗。</para>
    /// </summary>
    [JsonPropertyName("scopeContent")]
    // EN: legado scopeContent - rule applies to body text (default true, keeps legacy behavior).
    public bool ScopeContent { get; set; } = true;


    /// <summary>是否启用（旧键名 isEnabled，用 JsonPropertyName 固定输出）。</summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否正则替换（旧键名 isRegex；false = 字面替换）。</summary>
    [JsonPropertyName("isRegex")]
    public bool IsRegex { get; set; }

    /// <summary>执行顺序（升序；稳定排序，同值保持存储顺序）。</summary>
    public int Order { get; set; }
}

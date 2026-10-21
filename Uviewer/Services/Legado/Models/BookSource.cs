using System.Text.Json.Serialization;

namespace Uviewer.Services.Legado.Models;

/// <summary>
/// 书源顶层对象（Legado 书源 JSON 的根）。
/// <para>字段逐一对齐 reader/src/model/book_source.rs 与 legado BookSource.kt，
/// 因此同一份书源 JSON 可三端（reader / legado / Uviewer）互通，无需转换层。</para>
/// <para>序列化约定：属性用 PascalCase，经 <see cref="BookSourceJsonContext"/> 的
/// CamelCase 策略输出为 bookSourceUrl / bookSourceType 等 Legado 标准键名；
/// null 字段被省略（WhenWritingNull，与 reader serde(default) 一致）。</para>
/// <para>主键：<see cref="BookSourceUrl"/>。存储层（BookSourceManager）以它去重，
/// 故无主键的条目会被视为非法数据直接跳过（否则多条无主键书源会互相覆盖成一条）。</para>
/// </summary>
public sealed class BookSource
{
    /// <summary>书源地址（主键，唯一标识一个书源）。reader 内部用 book_source_url，序列化为 bookSourceUrl。</summary>
    public string BookSourceUrl { get; set; } = string.Empty;

    /// <summary>书源名称（UI 展示用）。</summary>
    public string BookSourceName { get; set; } = string.Empty;

    /// <summary>分组（逗号分隔的字符串，reader 按分组过滤/展示）。</summary>
    public string? BookSourceGroup { get; set; }

    /// <summary>类型：0 文本 / 1 音频 / 2 图片 / 3 文件 / 4 视频（见 <see cref="BookSourceKind"/>）。</summary>
    public int BookSourceType { get; set; }

    /// <summary>详情页 URL 正则（用于从任意 URL 反查是否属于本书，reader book_url_pattern）。</summary>
    public string? BookUrlPattern { get; set; }

    /// <summary>手动排序编号（null = 不参与手动排序）。</summary>
    public int? CustomOrder { get; set; }

    /// <summary>
    /// 是否启用。Legado 约定：缺省（null）即启用，仅显式 false 为禁用。
    /// 计算属性 <see cref="IsEnabled"/> 统一了这个三态语义，调用方不应直接读本属性。
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>启用发现页（explore）。</summary>
    public bool? EnabledExplore { get; set; }

    /// <summary>启用 CookieJar 自动保存每次请求的 cookie（reader enabled_cookie_jar）。</summary>
    public bool? EnabledCookieJar { get; set; }

    /// <summary>JS 库（注入到书源 JS 作用域的公共代码，如 java.ajax 的 polyfill）。</summary>
    public string? JsLib { get; set; }

    /// <summary>
    /// 并发率（"N" 串行 / "N/M" 窗口），对齐 reader concurrent_rate。
    /// 注意这是**限速**语义（毫秒/窗口），不是并发度；并发度由调用方给定（见 BookSourceLimits）。
    /// 阶段 4 的 ConcurrentRateLimiter 消费本字段，当前仅建模。
    /// </summary>
    public string? ConcurrentRate { get; set; }

    /// <summary>请求头（JSON 字符串形态，如 {"Referer":"https://..."}）。</summary>
    public string? Header { get; set; }

    /// <summary>登录地址（需要登录态的书源；阶段 4 WebView2 登录链路消费）。</summary>
    public string? LoginUrl { get; set; }

    /// <summary>登录 UI（字符串或 JSON 数组，描述登录表单字段）。</summary>
    public string? LoginUi { get; set; }

    /// <summary>登录检测 JS（返回真值表示已登录；阶段 4 消费）。</summary>
    public string? LoginCheckJs { get; set; }

    /// <summary>封面解密 JS（对封面字节做变换后再解码；阶段 3 图片管线消费）。</summary>
    public string? CoverDecodeJs { get; set; }

    /// <summary>书源注释（作者留言，如"需要登录"）。</summary>
    public string? BookSourceComment { get; set; }

    /// <summary>自定义变量说明（@put/@get 变量的文档字符串）。</summary>
    public string? VariableComment { get; set; }

    /// <summary>最后更新时间（排序用，毫秒时间戳）。</summary>
    public long LastUpdateTime { get; set; }

    /// <summary>响应时间（排序用，默认 180000；越小越优先）。</summary>
    public long RespondTime { get; set; } = 180000;

    /// <summary>智能排序权重（reader weight，越大越优先）。</summary>
    public int Weight { get; set; }

    /// <summary>发现页 URL 模板（含 {{key}}/{{page}} 等占位符）。</summary>
    public string? ExploreUrl { get; set; }

    /// <summary>发现筛选规则（JSON，描述筛选维度）。</summary>
    public string? ExploreScreen { get; set; }

    /// <summary>发现规则（结构与 <see cref="SearchRule"/> 完全一致，reader 中 ExploreRule = SearchRule）。</summary>
    public ExploreRule? RuleExplore { get; set; }

    /// <summary>搜索 URL 模板（含 {{key}}/{{page}} 等占位符；旧键 ruleSearchUrl 见 <see cref="LegacySearchUrl"/>）。</summary>
    public string? SearchUrl { get; set; }

    /// <summary>搜索规则（BookList 列表容器 + 9 个字段规则）。</summary>
    public SearchRule? RuleSearch { get; set; }

    /// <summary>书籍详情规则（init + 字段规则 + tocUrl）。</summary>
    public BookInfoRule? RuleBookInfo { get; set; }

    /// <summary>目录规则（chapterList + 章节字段规则 + nextTocUrl + formatJs）。</summary>
    public TocRule? RuleToc { get; set; }

    /// <summary>正文规则（content + title + nextContentUrl + 前后处理）。</summary>
    public ContentRule? RuleContent { get; set; }

    /// <summary>段评规则（legado 已弱化，仅建模备用）。</summary>
    public ReviewRule? RuleReview { get; set; }

    /// <summary>是否监听事件执行回调规则（reader event_listener）。</summary>
    public bool EventListener { get; set; }

    /// <summary>由书源控制的自定义按钮（reader custom_button）。</summary>
    public bool CustomButton { get; set; }

    /// <summary>首页模块定义（JSON 数组；阶段 5+ 消费）。</summary>
    public string? HomepageModules { get; set; }

    /// <summary>用 baseUrl 加载内容（reader load_with_base_url）。</summary>
    public bool? LoadWithBaseUrl { get; set; }

    /// <summary>单 URL 源（整个书源只有一个地址，如单篇小说页面）。</summary>
    public bool? SingleUrl { get; set; }

    // ---- 旧字段别名（Legado 历史键名）----
    // 机制：旧键由下面的别名属性直接用 [JsonPropertyName] 接收，反序列化后
    // LegacyAlias.Migrate 做 move_if_absent 合并（目标字段缺失才补齐），随后置 null。
    // 配合 WhenWritingNull 忽略策略，别名永不回写 —— 等价于旧版的字段迁移，
    // 但全程零 JsonNode DOM（性能架构的一部分，勿退回 JsonNode.Parse 方案）。

    /// <summary>旧键 "enable" → <see cref="Enabled"/>。</summary>
    [JsonPropertyName("enable")]
    public bool? LegacyEnabled { get; set; }

    /// <summary>旧键 "serialNumber" → <see cref="CustomOrder"/>。</summary>
    [JsonPropertyName("serialNumber")]
    public int? LegacyCustomOrder { get; set; }

    /// <summary>旧键 "ruleSearchUrl" → <see cref="SearchUrl"/>。</summary>
    [JsonPropertyName("ruleSearchUrl")]
    public string? LegacySearchUrl { get; set; }

    /// <summary>旧键 "ruleFindUrl" → <see cref="ExploreUrl"/>。</summary>
    [JsonPropertyName("ruleFindUrl")]
    public string? LegacyExploreUrl { get; set; }

    /// <summary>旧键 "ruleBookUrlPattern" → <see cref="BookUrlPattern"/>。</summary>
    [JsonPropertyName("ruleBookUrlPattern")]
    public string? LegacyBookUrlPattern { get; set; }

    /// <summary>计算属性：是否启用（缺省/true 均为启用）。不序列化。</summary>
    [JsonIgnore]
    public bool IsEnabled => Enabled != false;

    /// <summary>计算属性：类型枚举。不序列化。</summary>
    [JsonIgnore]
    public BookSourceKind SourceKind => (BookSourceKind)BookSourceType;
}

/// <summary>书源类型枚举，对齐 legado BookSourceType（0文/1音/2图/3文件/4视频）。</summary>
public enum BookSourceKind
{
    Text = 0,
    Audio = 1,
    Image = 2,
    File = 3,
    Video = 4,
}

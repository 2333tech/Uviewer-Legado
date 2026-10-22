namespace Uviewer.Services.Legado.Models;

/*
 * 漫画（图片书源）落盘模型。
 *
 * 存储形态（为什么是"打包"而不是"每页一个文件"）：
 *   一本漫画几百章、每章 20~60 页 —— 散图会产生上万个文件，严重污染磁盘
 *   （小文件簇浪费、备份/同步/杀毒全部变慢，资源管理器打开都卡）。
 *   因此每章打成一个 CBZ（ZIP 容器，阅读器原生支持，见 ArchiveSession），
 *   书级信息放一个 JSON 清单：
 *
 *     <下载目录>\Manga\<书名>_<hash8>\
 *         book.uvmanga          ← 书级清单（入口 / 目录 / 书源识别 / 阅读位置）
 *         ch0001_第1话.cbz      ← 章包：内嵌 ComicInfo.xml（标准漫画元数据）+ pages.json
 *         ch0002_第2话.cbz
 *
 *   CBZ 里的 ComicInfo.xml 是漫画界事实标准（Kavita/Komga/Calibre 都读），
 *   它承载"书源名 / 书源 URL / 书籍 URL / 章节序号 / 页数"等信息，
 *   即使清单 JSON 丢了，章包本身仍能自证出处。
 */

/// <summary>漫画落盘格式常量（扩展名、固定文件名、识别标记）。</summary>
public static class MangaFormat
{
    /// <summary>
    /// 漫画入口扩展名（类似 <c>.slnx</c>：一个文件声明"这个文件夹是一本漫画"）。
    /// <para>用它做入口而不是"遍历目录猜"——打开一张图片时不需要扫描父目录，
    /// 也不会把普通图片文件夹误判成漫画。</para>
    /// </summary>
    public const string ManifestExtension = ".uvmanga";

    /// <summary>入口固定文件名（书根目录下）。</summary>
    public const string ManifestFileName = "book" + ManifestExtension;

    /// <summary>章包扩展名（CBZ = ZIP，阅读器归档链路原生支持）。</summary>
    public const string ChapterArchiveExtension = ".cbz";

    /// <summary>章包下载中的临时后缀（下载完成才改名，避免把半包当完整章）。</summary>
    public const string ChapterArchivePartialExtension = ".cbz.part";

    /// <summary>章包内的漫画元数据文件名（ComicInfo 标准）。</summary>
    public const string ComicInfoEntryName = "ComicInfo.xml";

    /// <summary>章包内的机器可读页清单（URL ↔ 文件名，续下/换源用）。</summary>
    public const string PagesEntryName = "pages.json";

    /// <summary>书级清单的 kind 字段值（防误读同名 JSON）。</summary>
    public const string ManifestKind = "uvmanga";

    /// <summary>页清单的 kind 字段值。</summary>
    public const string ChapterKind = "uvmanga-chapter";

    /// <summary>当前格式版本（升级时按版本做兼容处理）。</summary>
    public const int Version = 1;
}

/// <summary>
/// 阅读位置（书级别）：第几章、章内第几页（0-based）。
/// <para>与文本阅读的"行号"等价物——漫画以"章 + 页"两段定位，
/// 因为章内页数会变（源站补图/删图），单用全局页号会错位。</para>
/// </summary>
public sealed class MangaPosition
{
    /// <summary>章节下标（对齐目录顺序，0-based）。</summary>
    public int ChapterIndex { get; set; }

    /// <summary>章内页下标（0-based；越界时读取端会钳制）。</summary>
    public int PageIndex { get; set; }
}

/// <summary>目录章条目（书级清单里的一行）。</summary>
public sealed class MangaChapterEntry
{
    /// <summary>章节下标（0-based，与目录顺序一致）。</summary>
    public int Index { get; set; }

    /// <summary>章节名（源站原文，未净化）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>章节页 URL（在线取图用；离线阅读不需要）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>章包文件名（相对书根目录；一旦写入不再改动，避免改名丢已下载的章包）。</summary>
    public string Archive { get; set; } = string.Empty;

    /// <summary>已落盘页数。</summary>
    public int Downloaded { get; set; }

    /// <summary>源站该章页数（未知为 0）。</summary>
    public int Pages { get; set; }

    /// <summary>是否整章打包齐全（等价于 <c>.cbz</c> 存在且页数达标）。</summary>
    public bool Complete { get; set; }

    /// <summary>是否分卷标题（非正文章节，不下载）。</summary>
    public bool IsVolume { get; set; }
}

/// <summary>
/// 书级清单（<c>book.uvmanga</c>）：漫画的唯一身份与目录来源。
/// <para>同时承担文本下载里"文件头标记 + sidecar"两件事：
/// 书源 URL / 书籍 URL 用于在线更新，UUID 用于改名/移动后重新关联，
/// 目录快照用于差分（新增章）判断，Position 用于断点续读。</para>
/// </summary>
public sealed class MangaManifest
{
    /// <summary>固定为 <see cref="MangaFormat.ManifestKind"/>（识别用）。</summary>
    public string Kind { get; set; } = MangaFormat.ManifestKind;

    /// <summary>格式版本。</summary>
    public int Version { get; set; } = MangaFormat.Version;

    /// <summary>唯一身份（新建时生成；移动/改名不变）。</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>书名。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>来源书源 URL（在线更新的第一把钥匙）。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>来源书源名（展示用，源被删也还知道出自哪里）。</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>书籍详情页 URL（在线更新的第二把钥匙）。</summary>
    public string BookUrl { get; set; } = string.Empty;

    /// <summary>封面图 URL（可选）。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>创建时间（ISO 8601 本地时间字符串）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>最近更新时间（目录刷新 / 追加章包时刷新）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>阅读位置。</summary>
    public MangaPosition Position { get; set; } = new();

    /// <summary>目录快照（按 <see cref="MangaChapterEntry.Index"/> 有序）。</summary>
    public List<MangaChapterEntry> Chapters { get; set; } = new();
}

/// <summary>章包内单张图片的记录。</summary>
public sealed class MangaImageRecord
{
    /// <summary>章包内文件名（如 <c>001.jpg</c>）。</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>原始图片 URL（防盗链重取/换源时唯一可靠线索）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>字节数（0 = 尚未下载成功）。</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// 章包内页清单（<c>pages.json</c>）：记录"这一章由书源哪一页得来、每张图对应哪个远程 URL"。
/// <para>与 <c>ComicInfo.xml</c> 分工：ComicInfo 给人/第三方阅读器看（标准元数据），
/// pages.json 给本程序做"续下只补缺"与换源排查（机器可读的 URL 映射）。</para>
/// </summary>
public sealed class MangaChapterImages
{
    /// <summary>固定为 <see cref="MangaFormat.ChapterKind"/>。</summary>
    public string Kind { get; set; } = MangaFormat.ChapterKind;

    /// <summary>格式版本。</summary>
    public int Version { get; set; } = MangaFormat.Version;

    /// <summary>章节下标（冗余存储：单独拷走一章也能看出是哪一章）。</summary>
    public int Index { get; set; }

    /// <summary>章节名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>章节页 URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>抓取时间（ISO 8601）。</summary>
    public string FetchedAt { get; set; } = string.Empty;

    /// <summary>是否整章齐全。</summary>
    public bool Complete { get; set; }

    /// <summary>图片记录（按页顺序）。</summary>
    public List<MangaImageRecord> Images { get; set; } = new();
}

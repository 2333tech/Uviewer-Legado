using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 净化规则（替换规则）的导入与清单数据源。
    ///
    /// <para><b>导入路径此前是缺失的</b>：<see cref="ReplaceRuleManager"/> 只能"加载自己的
    /// replaceRules.json"，用户无法把 legado / reader 导出的规则文件导进来——
    /// 净化功能等于没有入口。这里补上与书源导入同构的 FileOpenPicker + 流式导入。</para>
    ///
    /// <para><b>开关语义</b>：逐条 <c>IsEnabled</c>，勾选即落盘并刷新净化器快照。
    /// 刻意不提供"全局总开关"——逐条开关的语义单一，整体停用逐条取消即可
    /// （界面见 <see cref="RuleManagerDialog{T}"/>）。</para>
    /// </summary>
    public sealed class ReplaceRuleService
    {
        private readonly BookSourceService _service;
        private readonly FrameworkElement _rootElement;
        private readonly Func<nint> _windowHandleProvider;
        private readonly Action<string, string, string> _notify;

        public ReplaceRuleService(
            BookSourceService service,
            FrameworkElement rootElement,
            Func<nint> windowHandleProvider,
            Action<string, string, string> notify)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
            _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        }

        /// <summary>规则清单（供 RuleManagerDialog 罗列）。</summary>
        public IReadOnlyList<RuleListItem> List() => _service.ReplaceRules.GetAll()
            .Select(r => new RuleListItem(
                r.Id,
                NameOf(r),
                DetailOf(r),
                r.IsEnabled))
            .ToList();

        /// <summary>列表/表单共用的规则名（空名兜底为"(未命名规则)"）。</summary>
        public static string NameOf(ReplaceRule rule) => RuleEditSpec.ReplaceRuleName(rule);

        /// <summary>副标题：分组 / 作用范围 / 正则类型 / 匹配串摘要。</summary>
        public static string DetailOf(ReplaceRule rule) => RuleEditSpec.ReplaceRuleDetail(rule);

        // ============================================================
        // 规则管理（RuleManagerDialog 主从编辑界面）所需的读写操作
        // ============================================================

        /// <summary>深拷贝（进编辑时用：表单改副本，不污染正在净化的内存实例）。</summary>
        public static ReplaceRule Clone(ReplaceRule rule) => RuleEditSpec.Clone(rule);

        /// <summary>保存前校验（空匹配串 / 非法正则在此拦住，不给用户"存了却不生效"的谜）。</summary>
        public static string? Validate(ReplaceRule rule) => RuleEditSpec.Validate(rule);

        /// <summary>新建一条带默认值的净化规则（Id=0，保存时由存储层分配）。</summary>
        public ReplaceRule CreateNew() => RuleEditSpec.NewReplaceRule(_service.ReplaceRules.Count);

        /// <summary>按 Id 取存储中的规则（选中列表项时定位）。</summary>
        public ReplaceRule? GetById(long id) => _service.ReplaceRules.GetById(id);

        /// <summary>保存（新建或覆盖）：入库 + 落盘 + 刷新净化器快照。</summary>
        public async Task SaveAsync(ReplaceRule rule)
        {
            _service.ReplaceRules.Upsert(rule);
            await _service.ReplaceRules.SaveAsync().ConfigureAwait(true);
            ApplyRules();
        }

        /// <summary>删除：移除 + 落盘 + 刷新净化器快照。</summary>
        public async Task DeleteAsync(long id)
        {
            _service.ReplaceRules.Remove(id);
            await _service.ReplaceRules.SaveAsync().ConfigureAwait(true);
            ApplyRules();
        }

        /// <summary>切换单条规则的启用状态（落盘 + 刷新净化器）。</summary>
        public async Task SetEnabledAsync(long id, bool enabled)
        {
            await _service.ReplaceRules.SetEnabledAsync(id, enabled);
            ApplyRules();
        }

        /// <summary>导入后的净化器刷新（导入会改变规则集合，必须重灌快照）。</summary>
        public void ApplyRules() => _service.ApplyReplaceRules();

        /// <summary>
        /// 弹出文件选择器导入净化规则。用户取消返回 null。
        /// <para>文件选择部分与书源导入（<see cref="BookSourceImportService"/>）完全一致，
        /// 走同一个 <see cref="JsonPicker"/>；读入之后的处理（校验 / 落盘 / 刷新净化器）保持本类原逻辑。</para>
        /// </summary>
        public async Task<RuleImportResult?> PickAndImportAsync(CancellationToken token = default)
        {
            var path = await JsonPicker.PickAsync(_windowHandleProvider, _notify).ConfigureAwait(true);
            if (path is null)
            {
                return null;
            }

            return await ImportFileAsync(path, token).ConfigureAwait(true);
        }

        /// <summary>导入指定路径的 JSON（流式，零 DOM）。</summary>
        public async Task<RuleImportResult?> ImportFileAsync(string path, CancellationToken token = default)
        {
            if (!File.Exists(path))
            {
                _notify($"文件不存在：{path}", "\uE783", "Red");
                return null;
            }

            try
            {
                _notify($"正在导入 {Path.GetFileName(path)}…", "\uE8F1", "Gray");

                var result = await _service.ReplaceRules.ImportFromFileAsync(path, token);
                if (result.Total == 0)
                {
                    _notify($"导入失败：未识别到任何净化规则（{Path.GetFileName(path)}）", "\uE783", "Red");
                    return result;
                }

                await _service.ReplaceRules.SaveAsync(token);
                ApplyRules();

                var msg = $"净化规则导入完成：新增 {result.Added}，更新 {result.Updated}";
                if (result.Skipped > 0)
                {
                    msg += $"，跳过 {result.Skipped}（无匹配串）";
                }

                _notify(msg, "\uE73E", "Green");
                return result;
            }
            catch (OperationCanceledException)
            {
                _notify("导入已取消", "\uE711", "Gray");
                return null;
            }
            catch (Exception ex)
            {
                _notify($"导入失败：{ex.Message}", "\uE783", "Red");
                return null;
            }
        }

    }

    /// <summary>
    /// 标题 / 章节划分规则的导入与清单数据源（与 <see cref="ReplaceRuleService"/> 同构）。
    /// <para>差别只有一处：这里<b>不</b>接净化器（分章规则不影响正文净化），
    /// 但导入后需要让 TXT 重新生成目录，所以额外暴露 <see cref="RefreshToc"/> 回调。</para>
    /// </summary>
    public sealed class TitleRuleService
    {
        private readonly BookSourceService _service;
        private readonly FrameworkElement _rootElement;
        private readonly Func<nint> _windowHandleProvider;
        private readonly Action<string, string, string> _notify;
        private readonly Func<Task>? _refreshToc;

        public TitleRuleService(
            BookSourceService service,
            FrameworkElement rootElement,
            Func<nint> windowHandleProvider,
            Action<string, string, string> notify,
            Func<Task>? refreshToc = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
            _notify = notify ?? throw new ArgumentNullException(nameof(notify));
            _refreshToc = refreshToc;
        }

        /// <summary>规则清单（供 RuleManagerDialog 罗列）。</summary>
        public IReadOnlyList<RuleListItem> List() => _service.TitleRules.GetAll()
            .Select(r => new RuleListItem(
                r.Id,
                NameOf(r),
                DetailOf(r),
                r.IsEnabled))
            .ToList();

        /// <summary>列表/表单共用的规则名（空名兜底为"(未命名规则)"）。</summary>
        public static string NameOf(TitleRule rule) => RuleEditSpec.TitleRuleName(rule);

        /// <summary>副标题：分组 / 规则种类 / 匹配串摘要。</summary>
        public static string DetailOf(TitleRule rule) => RuleEditSpec.TitleRuleDetail(rule);

        // ============================================================
        // 规则管理（RuleManagerDialog 主从编辑界面）所需的读写操作
        // ============================================================

        /// <summary>深拷贝（进编辑时用：表单改副本，不污染正在切分的内存实例）。</summary>
        public static TitleRule Clone(TitleRule rule) => RuleEditSpec.Clone(rule);

        /// <summary>保存前校验（空正则且非书名行 / 非法正则会在此拦住）。</summary>
        public static string? Validate(TitleRule rule) => RuleEditSpec.Validate(rule);

        /// <summary>新建一条带默认值的标题规则（预填常见中文写法正则，Id=0）。</summary>
        public TitleRule CreateNew() => RuleEditSpec.NewTitleRule(_service.TitleRules.Count);

        /// <summary>按 Id 取存储中的规则（选中列表项时定位）。</summary>
        public TitleRule? GetById(long id) => _service.TitleRules.GetById(id);

        /// <summary>保存（新建或覆盖）：入库 + 落盘 + 刷新 TXT 目录。</summary>
        public async Task SaveAsync(TitleRule rule)
        {
            _service.TitleRules.Upsert(rule);
            await _service.TitleRules.SaveAsync().ConfigureAwait(true);
            await RefreshTocAsync().ConfigureAwait(true);
        }

        /// <summary>删除：移除 + 落盘 + 刷新 TXT 目录。</summary>
        public async Task DeleteAsync(long id)
        {
            _service.TitleRules.Remove(id);
            await _service.TitleRules.SaveAsync().ConfigureAwait(true);
            await RefreshTocAsync().ConfigureAwait(true);
        }

        /// <summary>切换单条规则启用状态（落盘）。</summary>
        public async Task SetEnabledAsync(long id, bool enabled)
        {
            await _service.TitleRules.SetEnabledAsync(id, enabled);
            await RefreshTocAsync().ConfigureAwait(true);
        }

        /// <summary>弹出文件选择器导入标题规则。用户取消返回 null。</summary>
        public async Task<RuleImportResult?> PickAndImportAsync(CancellationToken token = default)
        {
            var path = await JsonPicker.PickAsync(_windowHandleProvider, _notify).ConfigureAwait(true);
            if (path is null)
            {
                return null;
            }

            return await ImportFileAsync(path, token).ConfigureAwait(true);
        }

        /// <summary>导入指定路径的 JSON。</summary>
        public async Task<RuleImportResult?> ImportFileAsync(string path, CancellationToken token = default)
        {
            if (!File.Exists(path))
            {
                _notify($"文件不存在：{path}", "\uE783", "Red");
                return null;
            }

            try
            {
                _notify($"正在导入 {Path.GetFileName(path)}…", "\uE8F1", "Gray");

                var result = await _service.TitleRules.ImportFromFileAsync(path, token);
                if (result.Total == 0)
                {
                    _notify($"导入失败：未识别到任何标题规则（{Path.GetFileName(path)}）", "\uE783", "Red");
                    return result;
                }

                await _service.TitleRules.SaveAsync(token);
                await RefreshTocAsync().ConfigureAwait(true);

                var msg = $"标题规则导入完成：新增 {result.Added}，更新 {result.Updated}";
                if (result.Skipped > 0)
                {
                    msg += $"，跳过 {result.Skipped}（无匹配式）";
                }

                _notify(msg, "\uE73E", "Green");
                return result;
            }
            catch (OperationCanceledException)
            {
                _notify("导入已取消", "\uE711", "Gray");
                return null;
            }
            catch (Exception ex)
            {
                _notify($"导入失败：{ex.Message}", "\uE783", "Red");
                return null;
            }
        }

        private async Task RefreshTocAsync()
        {
            if (_refreshToc is null)
            {
                return;
            }

            try
            {
                await _refreshToc().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notify($"刷新目录失败：{ex.Message}", "\uE783", "Red");
            }
        }
    }
}

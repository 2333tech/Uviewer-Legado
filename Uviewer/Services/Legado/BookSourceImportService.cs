using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Services;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源 JSON 导入：选文件 → 流式导入 → 反馈新增/更新/跳过数量。
    ///
    /// <para><b>为什么走库侧流式导入</b>：多书源文件常有 5MB / 400+ 条（Sample 即如此），
    /// <c>ImportFromFileAsync</c> 用 UTF-8 结构扫描切元素切片 + 256 条/批合入，
    /// 峰值内存有界；先整份读进内存再反序列化会瞬时占用数倍体积。</para>
    ///
    /// <para><b>为什么不"先校验再导入"</b>：整份预读会抵消流式的内存优势。
    /// 库侧导入本身对损坏条目是容错的（跳过而非整体失败），导入后再把结果
    /// （新增/更新/跳过）如实呈现给用户即可。</para>
    /// </summary>
    public sealed class BookSourceImportService
    {
        private readonly BookSourceService _service;
        private readonly Func<nint> _windowHandleProvider;
        private readonly FrameworkElement _rootElement;
        private readonly Action<string, string, string> _notify;

        public BookSourceImportService(
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

        /// <summary>
        /// 弹出文件选择器导入书源。返回导入结果；用户取消返回 null。
        /// </summary>
        public async Task<ImportResult?> PickAndImportAsync(CancellationToken token = default)
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Downloads,
            };

            try
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());
            }
            catch
            {
                // 拿不到窗口句柄时 picker 可能无法弹出；不致命，继续尝试
            }

            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return null;   // 用户取消
            }

            // WinRT StorageFile → 本地路径（库侧按流式文件读取，需要真实路径）
            string path;
            try
            {
                path = file.Path;
            }
            catch
            {
                _notify("无法读取所选文件的路径", "\uE783", "Red");
                return null;
            }

            return await ImportFileAsync(path, token);
        }

        /// <summary>
        /// 导入指定路径的 JSON 文件并给出反馈。捕获全部异常并转成提示，
        /// 保证"导入失败"不会把应用带崩（用户可能误选任意 JSON）。
        /// </summary>
        public async Task<ImportResult?> ImportFileAsync(string path, CancellationToken token = default)
        {
            if (!File.Exists(path))
            {
                _notify($"文件不存在：{path}", "\uE783", "Red");
                return null;
            }

            try
            {
                // 进度提示：400+ 源的大文件解析需要一点时间，先让用户知道在动
                _notify($"正在导入 {Path.GetFileName(path)}…", "\uE8F1", "Gray");

                var result = await _service.ImportFromFileAsync(path, token);

                if (result.Total == 0)
                {
                    _notify(
                        $"导入失败：未识别到任何书源（{Path.GetFileName(path)} 不是书源数组文件？）",
                        "\uE783",
                        "Red");
                    return result;
                }

                var message = $"导入完成：新增 {result.Added}，更新 {result.Updated}";
                if (result.Skipped > 0)
                {
                    message += $"，跳过 {result.Skipped}";
                }

                _notify(message, "\uE73E", "Green");
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
}
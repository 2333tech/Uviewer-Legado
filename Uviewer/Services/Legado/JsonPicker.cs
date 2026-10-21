using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// WinUI3 的文件 / 目录选择器（FileOpenPicker、FolderPicker）统一入口。
    ///
    /// <para><b>为什么必须封装</b>：净化规则与标题规则两处各写一份 picker 逻辑，
    /// 其中一处漏了句柄校验、另一处静默吞异常——两份实现漂移正是这个 bug 的温床。
    /// 抽成一处后，句柄来源、错误提示、失败行为都只有一份定义。</para>
    ///
    /// <para><b>句柄必须来自真正的 <c>Window</c></b>：
    /// <c>WinRT.Interop.WindowNative.GetWindowHandle</c> 只有一个 <c>(object)</c> 重载，
    /// 内部转 <c>IWindowNative</c> 接口。WinUI3 的 <c>Window</c> 实现了它，
    /// 而 WinAppSDK 的 <c>Microsoft.UI.Windowing.AppWindow</c> <b>没有</b>——
    /// 拿 AppWindow 去换会得到无效句柄，picker 随即抛
    /// <c>Invalid window handle (0x80070578) Consider WindowNative, InitializeWithWindow</c>。
    /// 宿主已在 <c>MainWindow</c> 侧取好正确句柄并通过端口传进来。</para>
    ///
    /// <para><b>失败必须让用户看见</b>：句柄取不到或绑定失败时给出明确提示，
    /// 绝不静默继续——静默失败会留下一句指向错误方向的 COM 报错，浪费大量排查时间。</para>
    /// </summary>
    internal static class JsonPicker
    {
        /// <summary>
        /// 弹出文件选择器选一个 JSON 文件，返回其本地路径。
        /// <para>用户取消、句柄不可用、路径读取失败 → 返回 null（失败原因已通过
        /// <paramref name="notify"/> 告知用户）。</para>
        /// </summary>
        public static async Task<string?> PickAsync(
            Func<nint> windowHandleProvider,
            Action<string, string, string> notify)
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Downloads,
            };

            if (!TryBindToWindow(picker, windowHandleProvider, notify, "文件选择器"))
            {
                return null;
            }

            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return null;   // 用户取消
            }

            try
            {
                return file.Path;
            }
            catch (Exception ex)
            {
                notify($"无法读取所选文件的路径：{ex.Message}", "\uE783", "Red");
                return null;
            }
        }

        /// <summary>
        /// 把 picker 绑定到主窗口。
        /// <para>句柄为 0 或绑定抛异常 → 明确报错并返回 false，
        /// <b>不静默继续</b>（曾经用 <c>catch { }</c> 兜底，把真正原因藏了起来）。</para>
        /// <para>参数取 <see cref="object"/>：<c>FileOpenPicker</c>/<c>FolderPicker</c>/<c>FileSavePicker</c>
        /// 都实现同一个 <c>IInitializeWithWindow</c> 契约，共用一份绑定逻辑才不会几处各写一遍。</para>
        /// </summary>
        private static bool TryBindToWindow(
            object picker,
            Func<nint> windowHandleProvider,
            Action<string, string, string> notify,
            string kind)
        {
            try
            {
                var hwnd = windowHandleProvider();
                if (hwnd == nint.Zero)
                {
                    notify($"无法获取窗口句柄，{kind}无法弹出", "\uE783", "Red");
                    return false;
                }

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                return true;
            }
            catch (Exception ex)
            {
                notify($"{kind}初始化失败：{ex.Message}", "\uE783", "Red");
                return false;
            }
        }

        /// <summary>
        /// 弹出<b>目录</b>选择器（Windows 资源管理器风格的文件夹选取），返回所选目录的完整路径。
        /// <para>与 <see cref="PickAsync"/> 共用同一套句柄绑定与错误提示——下载设置里的"浏览"
        /// 按钮正是复用它，所以不允许在这里另写一份 picker 逻辑。</para>
        /// <para>用户取消 / 句柄不可用 / 路径读取失败 → 返回 null（失败原因已提示）。</para>
        /// </summary>
        /// <param name="initialDirectory">期望的起始目录（不存在时忽略，退回"下载"文件夹）。</param>
        public static async Task<string?> PickFolderAsync(
            Func<nint> windowHandleProvider,
            Action<string, string, string> notify,
            string? initialDirectory = null)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                CommitButtonText = "选择此文件夹",
            };

            if (!TryBindToWindow(picker, windowHandleProvider, notify, "目录选择器"))
            {
                return null;
            }

            // FolderPicker 在部分 Windows 版本上要求过滤列表非空，空列表会抛
            // "The FileTypeFilter collection must not be empty"；"*" 表示不限类型。
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return null;   // 用户取消
            }

            try
            {
                return folder.Path;
            }
            catch (Exception ex)
            {
                notify($"无法读取所选目录的路径：{ex.Message}", "\uE783", "Red");
                return null;
            }
        }
    }
}

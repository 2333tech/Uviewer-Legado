using System;
using System.IO;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 整本下载目录的路径规则：默认值、校验、规范化。
    ///
    /// <para><b>为什么单独成文件</b>：这些规则是纯字符串/路径逻辑，不带任何 WinUI 依赖，
    /// 因此可以被集成测试工程直接链接（同 <c>TextTocBuilder</c> / <c>TextCacheModels</c> 的做法）。
    /// 把这套规则留在 <c>BookSourceService</c>（依赖 WinUI 宿主类型）里就永远测不到——
    /// 而"用户输入了半个路径/带引号/带 <c>%USERPROFILE%</c>/指向一个文件"正是最容易出问题的地方。</para>
    ///
    /// <para><b>默认值为什么是用户"下载"文件夹</b>：Windows 上没有 <c>SpecialFolder.Downloads</c>
    /// （known folder 未进 BCL 枚举），按 <c>%USERPROFILE%\Downloads</c> 拼接与资源管理器一致；
    /// 用户对"下载"的自然预期就是这个目录，而不是 <c>%LocalAppData%\Uviewer\books</c> 这种隐藏路径。</para>
    /// </summary>
    internal static class DownloadPathRules
    {
        /// <summary>默认下载目录：<c>%USERPROFILE%\Downloads</c>。</summary>
        public static string DefaultDirectory
        {
            get
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Uviewer", "books")
                    : Path.Combine(profile, "Downloads");
            }
        }

        /// <summary>
        /// 校验并规范化用户输入的下载目录：
        /// 去首尾空白与包裹引号（资源管理器"复制路径"会带引号）→ 展开 <c>%VAR%</c> →
        /// <see cref="Path.GetFullPath(string)"/> 归一（消 <c>..</c>、补盘符、统一分隔符）。
        /// <para>不要求目录已存在（首次下载时才创建）；但<b>已存在的同名文件</b>要拒绝——
        /// 那是笔误，不是目录。</para>
        /// </summary>
        /// <exception cref="ArgumentException">路径为空、非法或指向一个文件。</exception>
        public static string Normalize(string? path)
        {
            var text = (path ?? string.Empty).Trim().Trim('"', '\'');
            if (text.Length == 0)
            {
                throw new ArgumentException("下载目录不能为空");
            }

            text = Environment.ExpandEnvironmentVariables(text);

            // 必须在 GetFullPath 之前判断：GetFullPath 会把相对路径按当前工作目录补成绝对路径，
            // 于是"用户只填了 Books"这种输入会被静默解释成"进程工作目录下的 Books"——
            // 文件落到安装目录旁边，用户永远找不到。要求完整路径是最不意外的语义。
            if (!Path.IsPathRooted(text))
            {
                throw new ArgumentException("下载目录必须是完整路径（含盘符，如 D:\\Books）");
            }

            string full;
            try
            {
                full = Path.GetFullPath(text);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"下载目录不是合法的完整路径：{ex.Message}");
            }

            if (File.Exists(full))
            {
                throw new ArgumentException("该路径是一个文件，不是目录");
            }

            return Path.TrimEndingDirectorySeparator(full);
        }

        /// <summary>
        /// 取"最终生效"的下载目录：配置值合法就用它，否则退回
        /// <see cref="DefaultDirectory"/>。配置文件被手改坏不该让下载直接不可用。
        /// </summary>
        public static string Resolve(string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                return DefaultDirectory;
            }

            try
            {
                return Normalize(configured);
            }
            catch (ArgumentException)
            {
                return DefaultDirectory;
            }
        }
    }
}

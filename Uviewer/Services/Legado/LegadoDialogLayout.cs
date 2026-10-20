using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 弹窗尺寸计算：把"内容想要多宽"换算成"对话框该设多宽"，并按窗口实际大小收敛。
    ///
    /// <para><b>为什么需要它</b>：WinUI 的 <c>ContentDialog</c> 模板写死了两个默认值
    /// （取自 <c>generic.xaml</c>：<c>ContentDialogMaxWidth=548</c>、<c>ContentDialogPadding=24</c>），
    /// 于是<b>内容可用宽度只有 548 − 48 = 500px</b>。书源管理的内容要 900px
    /// （左列表 280 + 右侧编辑区），不显式设 <c>MaxWidth</c> 就会被裁掉 400px ——
    /// 表现就是"点开书源后编辑框超出窗口"。</para>
    ///
    /// <para><b>设计原则：加大窗口，不缩内容</b>。本类只做两件事：
    /// ① 把对话框放宽到<b>装得下内容</b>；② 若窗口本身不够宽，则以窗口为准，
    /// 此时内容区用 <c>Star</c> 列 + <c>TextTrimming</c> 收缩，而<b>不是</b>
    /// 把输入框、字号这些内容尺寸调小 —— 后者会让"书源 URL / 规则正则"这类
    /// 长文本更难读，而长文本可读性恰恰是书源管理界面存在的意义。</para>
    ///
    /// <para><b>为什么不硬编码 MinWidth</b>：写死 <c>MinWidth=656</c> 在窄窗口
    /// （分屏、小窗、 maximized 未满屏）下对话框会比窗口还宽，同样被裁。
    /// 故本类一律按 <see cref="XamlRoot.Size"/> 动态计算上下限。</para>
    /// </summary>
    internal static class LegadoDialogLayout
    {
        /// <summary>对话框左右内边距合计（ContentDialogPadding=24 × 2）。</summary>
        private const double HorizontalPadding = 48;

        /// <summary>对话框与窗口边缘保留的呼吸空间。</summary>
        private const double WindowMargin = 32;

        /// <summary>对话框标题 + 命令区 + 内边距的固定高度开销（实测约 154px）。</summary>
        private const double VerticalChrome = 154;

        /// <summary>
        /// 给对话框设置尺寸：放宽到装得下内容，并按窗口实际大小收敛。
        /// </summary>
        /// <param name="dialog">目标对话框。</param>
        /// <param name="rootElement">用于取窗口尺寸与主题的根元素。</param>
        /// <param name="desiredContentWidth">内容想要的总宽度（px）。</param>
        public static void Fit(
            ContentDialog dialog,
            FrameworkElement rootElement,
            double desiredContentWidth)
        {
            var window = rootElement.XamlRoot?.Size ?? default;

            // 窗口尺寸拿不到（XamlRoot 未就绪）时退回一个保守的可用值
            var windowWidth = window.Width > 0 ? window.Width : 1280;
            var windowHeight = window.Height > 0 ? window.Height : 800;

            // ① 目标宽 = 内容宽 + 内边距，但不超过窗口可用宽
            var maxByContent = desiredContentWidth + HorizontalPadding;
            var maxByWindow = windowWidth - WindowMargin;
            var width = Math.Clamp(maxByContent, 320, Math.Max(320, maxByWindow));

            // ② 内容高同理；留出标题/命令区/内边距
            var maxHeight = Math.Max(240, windowHeight - WindowMargin - VerticalChrome);

            dialog.MaxWidth = width;
            dialog.MinWidth = Math.Min(width, 320);

            // ③ 高度不设死：交给内容自身决定，只给一个不超过窗口的上限。
            //    设 MinHeight 会让小内容对话框被强行拉高（底部大片空白）。
            dialog.MaxHeight = maxHeight;
        }

        /// <summary>
        /// 计算内部滚动面板可用的最大高度（已扣除对话框标题/命令区/内边距与窗口边距）。
        /// <para>供 <c>ScrollViewer.MaxHeight</c> 使用，避免纵向也溢出窗口。</para>
        /// </summary>
        public static double ScrollViewportHeight(
            FrameworkElement rootElement,
            double preferred)
        {
            var windowHeight = rootElement.XamlRoot?.Size.Height ?? 0;
            if (windowHeight <= 0)
            {
                return preferred;
            }

            var available = windowHeight - WindowMargin - VerticalChrome;
            return Math.Clamp(preferred, 200, Math.Max(200, available));
        }
    }
}

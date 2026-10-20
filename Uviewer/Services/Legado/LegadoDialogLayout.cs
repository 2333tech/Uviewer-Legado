using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 弹窗尺寸计算：把"内容想要多宽/多高"换算成"对话框视觉边框该设多大"，并按窗口实际大小收敛。
    ///
    /// <para><b>核心机制（改尺寸前必读，踩过坑）</b>：WinUI 的 <c>ContentDialog</c> 模板
    /// （generic.xaml 中 <c>BackgroundElement</c>）的四个尺寸属性全部是
    /// <c>{ThemeResource ContentDialogMinWidth/MaxWidth/MinHeight/MaxHeight}</c>
    /// （默认 <b>320 / 548 / 184 / 756</b>），<b>不是</b> <c>{TemplateBinding MaxWidth}</c>——
    /// 也就是说 <c>dialog.MaxWidth</c> 等<b>属性设置对视觉尺寸完全无效</b>！</para>
    ///
    /// <para><b>只设属性不仅无效，还有反效果</b>：对话框控件本身是被宿主<b>拉伸至整个窗口</b>的
    /// （模板里的烟雾层 <c>SmokeLayerBackground</c> 要盖住全窗口才像模态），此时用
    /// MaxWidth/MaxHeight 属性去裁这个控件，等于把拉伸中的控件按上限裁小——烟雾层随之缺角，
    /// 视觉边框（模板内 Center/Center 对齐）改为在裁小的控件里居中，弹窗整体偏离窗口中心。
    /// 实测表现正是"编辑框超出弹窗 / 弹窗不在主窗口中央"。</para>
    ///
    /// <para><b>正确做法</b>：在对话框<b>自己的 Resources</b> 里覆盖上述资源键
    /// （ThemeResource 查找先命中元素局部资源，故模板内的 Border 能拿到新值）。
    /// 应用内 <c>MainToolbarControl</c> 的工具栏定制弹窗用的就是这一套
    /// （<c>dialog.Resources["ContentDialogMaxWidth"] = ...</c>）。</para>
    ///
    /// <para><b>顺带解决居中</b>：模板的 <c>BackgroundElement</c> 本身就是
    /// <c>HorizontalAlignment="Center" VerticalAlignment="Center"</c>，且被不设上限的
    /// 全窗口控件包着——只要不用属性裁控件，弹窗自动在主窗口正中。
    /// <b>不要</b>再去设 dialog 的 HorizontalAlignment/VerticalAlignment（会把控件改成
    /// 内容尺寸，烟雾层直接缩成弹窗大小）。</para>
    ///
    /// <para><b>设计原则：加大窗口，不缩内容</b>。本类只做两件事：
    /// ① 把视觉边框放宽到<b>装得下内容</b>；② 若窗口本身不够宽/高，则以窗口为准，
    /// 此时内容区用 <c>Star</c> 列 + <c>TextTrimming</c> 收缩，而<b>不是</b>
    /// 把输入框、字号这些内容尺寸调小 —— 后者会让"书源 URL / 规则正则"这类
    /// 长文本更难读，而长文本可读性恰恰是书源/规则管理界面存在的意义。</para>
    /// </summary>
    internal static class LegadoDialogLayout
    {
        /// <summary>对话框左右内边距合计（ContentDialogPadding=24 × 2）。</summary>
        private const double HorizontalPadding = 48;

        /// <summary>对话框与窗口边缘保留的呼吸空间。</summary>
        private const double WindowMargin = 32;

        /// <summary>对话框标题 + 命令区 + 内边距的固定高度开销（实测约 154px）。</summary>
        private const double VerticalChrome = 154;

        /// <summary>视觉边框的高度下限（防止窄内容对话框被压成一条）。</summary>
        private const double MinBorderHeight = 240;

        /// <summary>
        /// 给对话框设置尺寸：放宽到装得下内容，并按窗口实际大小收敛。
        /// <para>宽度取<b>精确值</b>（MinWidth 资源 = MaxWidth 资源 = width），
        /// 与工具栏定制弹窗一致——三个管理类对话框同宽，观感统一；
        /// 高度只设上限（内容矮就矮着，不强行拉高留下大片空白）。</para>
        /// </summary>
        /// <param name="dialog">目标对话框。</param>
        /// <param name="rootElement">用于取窗口尺寸与主题的根元素。</param>
        /// <param name="desiredContentWidth">内容想要的总宽度（px）。</param>
        /// <param name="preferredContentHeight">内部滚动区的期望高度（px）；
        /// 视觉边框总高 = 该值 + <see cref="VerticalChrome"/>，调用方给内部
        /// ScrollViewer 设 MaxHeight 时应传同一个值（见 <see cref="ScrollViewportHeight"/>）。</param>
        public static void Fit(
            ContentDialog dialog,
            FrameworkElement rootElement,
            double desiredContentWidth,
            double preferredContentHeight = 620)
        {
            var window = rootElement.XamlRoot?.Size ?? default;

            // 窗口尺寸拿不到（XamlRoot 未就绪）时退回一个保守的可用值
            var windowWidth = window.Width > 0 ? window.Width : 1280;
            var windowHeight = window.Height > 0 ? window.Height : 800;

            // ① 目标宽 = 内容宽 + 内边距，但不超过窗口可用宽
            var maxByContent = desiredContentWidth + HorizontalPadding;
            var maxByWindow = windowWidth - WindowMargin;
            var width = Math.Clamp(maxByContent, 320, Math.Max(320, maxByWindow));

            // ② 视觉边框总高 = 内部滚动区期望高 + 标题/命令区/内边距开销，
            //    同样不超过窗口可用高（ScrollViewer 的 MaxHeight 与这里必须同源，
            //    否则边框被自己的 MaxHeight 资源截断，底部内容会被吃掉一截）
            var viewport = ScrollViewportHeight(rootElement, preferredContentHeight);
            var height = Math.Min(
                viewport + VerticalChrome,
                Math.Max(MinBorderHeight, windowHeight - WindowMargin));

            // ③ 尺寸走资源键（模板用 ThemeResource 读），不设属性——原因见类注释
            dialog.Resources["ContentDialogMinWidth"] = width;
            dialog.Resources["ContentDialogMaxWidth"] = width;
            dialog.Resources["ContentDialogMaxHeight"] = height;
        }

        /// <summary>
        /// 计算内部滚动面板可用的最大高度（已扣除对话框标题/命令区/内边距与窗口边距）。
        /// <para>供 <c>ScrollViewer.MaxHeight</c> 使用，避免纵向溢出窗口。
        /// 与 <see cref="Fit"/> 内部计算同源：<c>Fit</c> 的视觉边框总高 =
        /// 本函数返回值 + <see cref="VerticalChrome"/>。</para>
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

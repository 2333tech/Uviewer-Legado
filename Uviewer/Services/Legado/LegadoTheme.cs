using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 弹窗类界面的配色方案。
    /// <para>按 <see cref="ElementTheme"/>（亮/暗）给出<b>成对的</b>前景/背景，
    /// 保证任何主题下文字与底色都有足够对比度。</para>
    /// </summary>
    /// <param name="Surface">卡片/行底色。</param>
    /// <param name="SurfaceBorder">卡片描边。</param>
    /// <param name="TextPrimary">主文字（规则名、书名）。</param>
    /// <param name="TextSecondary">次要文字（分组、匹配串摘要、状态提示）。</param>
    /// <param name="TextMuted">最弱一档文字（占位、空态提示）。</param>
    /// <param name="Accent">强调色（主要按钮底色）。</param>
    /// <param name="OnAccent">强调色之上的文字色。</param>
    /// <param name="Success">成功/完成态按钮底色。</param>
    /// <param name="OnSuccess">成功态按钮文字色。</param>
    /// <param name="Warning">警示色（下载中/可取消）。</param>
    /// <param name="OnWarning">警示态按钮文字色。</param>
    /// <param name="Critical">错误语义色（Win11 SystemFillColorCritical：无法访问的结果标记）。</param>
    /// <param name="Caution">警告语义色（Win11 SystemFillColorCaution：可访问但内容有问题的标记、⚠ 徽标）。</param>
    public readonly record struct LegadoPalette(
        Brush Surface,
        Brush SurfaceBorder,
        Brush TextPrimary,
        Brush TextSecondary,
        Brush TextMuted,
        Brush Accent,
        Brush OnAccent,
        Brush Success,
        Brush OnSuccess,
        Brush Warning,
        Brush OnWarning,
        Brush Critical,
        Brush Caution);

    /// <summary>
    /// 弹窗配色：<b>按主题显式取值</b>，不依赖主题资源查找。
    ///
    /// <para><b>为什么不用 <c>Application.Resources</c> 查主题画刷</b>：
    /// 主题画刷（如 <c>TextFillColorSecondaryBrush</c>）定义在 XAML 资源字典的
    /// ThemeDictionaries 里，<c>TryGetValue</c> 未必能取到；取不到时若回退到
    /// 硬编码的 <c>Colors.White</c> / <c>Colors.LightGray</c>，在<b>亮色</b>主题下
    /// 就变成"白字画在白底上"——完全看不见。实测这正是规则查看页"提示文字与主题同色、
    /// 可读性极低"的成因。</para>
    ///
    /// <para><b>本方案的特点</b>：
    /// <list type="bullet">
    /// <item>亮/暗两套值<b>成对定义</b>，对比度可预期，不依赖任何资源查找是否成功；</item>
    /// <item>文字色一律<b>灰度</b>（R=G=B），不带色相——彩色只留给语义化的
    /// <b>Emoji 图形</b>（如 ⚠ 徽标）与按钮底色，避免"提示文字是蓝的/红的"这类
    /// 与主题打架的观感；</item>
    /// <item>切换主题时重新取一次即可（见 <see cref="For(FrameworkElement)"/>）。</item>
    /// </list></para>
    /// </summary>
    internal static class LegadoTheme
    {
        // ── 亮色主题 ──────────────────────────────────────────────
        // 卡片 #FFFFFF 铺在对话框底 #F3F3F3 上，边界清晰
        private static readonly LegadoPalette LightPalette = new(
            Surface: Rgb(0xFF, 0xFF, 0xFF),
            SurfaceBorder: Rgb(0xE0, 0xE0, 0xE0),
            TextPrimary: Rgb(0x1A, 0x1A, 0x1A),      // 对比度 ≈ 16.5:1
            TextSecondary: Rgb(0x59, 0x59, 0x59),    // 对比度 ≈ 7.0:1
            TextMuted: Rgb(0x76, 0x76, 0x76),        // 对比度 ≈ 4.6:1
            Accent: Rgb(0x00, 0x5F, 0xB8),
            OnAccent: Rgb(0xFF, 0xFF, 0xFF),
            Success: Rgb(0x0F, 0x6C, 0x3C),
            OnSuccess: Rgb(0xFF, 0xFF, 0xFF),
            Warning: Rgb(0xB4, 0x24, 0x24),
            OnWarning: Rgb(0xFF, 0xFF, 0xFF),
            // Win11 Fluent 语义色（亮色）：Critical #C50F1F / Caution #9D5D00
            Critical: Rgb(0xC5, 0x0F, 0x1F),
            Caution: Rgb(0x9D, 0x5D, 0x00));

        // ── 暗色主题 ──────────────────────────────────────────────
        // 卡片 #383838 铺在对话框底 #2C2C2C 上，靠亮度差区分层次
        private static readonly LegadoPalette DarkPalette = new(
            Surface: Rgb(0x38, 0x38, 0x38),
            SurfaceBorder: Rgb(0x4A, 0x4A, 0x4A),
            TextPrimary: Rgb(0xFF, 0xFF, 0xFF),      // 对比度 ≈ 11.6:1
            TextSecondary: Rgb(0xC8, 0xC8, 0xC8),    // 对比度 ≈ 8.0:1
            TextMuted: Rgb(0xA6, 0xA6, 0xA6),        // 对比度 ≈ 5.4:1
            Accent: Rgb(0x60, 0xCD, 0xFF),
            OnAccent: Rgb(0x00, 0x00, 0x00),
            Success: Rgb(0x6C, 0xCB, 0x5F),
            OnSuccess: Rgb(0x10, 0x20, 0x10),
            Warning: Rgb(0xC4, 0x2B, 0x1C),
            OnWarning: Rgb(0xFF, 0xFF, 0xFF),
            // Win11 Fluent 语义色（暗色）：Critical #FF99A4 / Caution #FCE100
            Critical: Rgb(0xFF, 0x99, 0xA4),
            Caution: Rgb(0xFC, 0xE1, 0x00));

        /// <summary>取指定主题的配色。</summary>
        public static LegadoPalette For(ElementTheme theme) =>
            theme == ElementTheme.Dark ? DarkPalette : LightPalette;

        /// <summary>
        /// 按元素<b>实际</b>主题取配色。
        /// <para>用实际主题而非应用主题：弹窗可以设 <c>RequestedTheme</c> 覆盖，
        /// 此时元素实际主题才是最终生效的那个。</para>
        /// </summary>
        public static LegadoPalette For(FrameworkElement element) =>
            For(element?.ActualTheme ?? ElementTheme.Light);

        /// <summary>
        /// 纯色（可带 alpha）。
        /// <para>所有<b>文字</b>色都必须是灰度（R=G=B）——彩色只留给语义 Emoji
        /// 与按钮底色，这样提示文字在任何主题下都不会与背景"撞色"。</para>
        /// </summary>
        public static Brush Rgb(byte r, byte g, byte b, byte a = 0xFF) =>
            new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}

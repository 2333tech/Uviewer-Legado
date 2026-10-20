using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 弹窗编辑表单的<b>共用构件</b>（标签 + 输入框的一行单元）。
    ///
    /// <para><b>为什么抽出来</b>：书源管理（<see cref="BookSourceManagerDialog"/>）与
    /// 规则管理（<see cref="RuleManagerDialog{T}"/>）都是"主从布局 + 反射表单"，
    /// 字段构件的实现必须只有一份——否则两处各写一遍，改间距/改只读语义时必然漏一处。
    /// 规则界面不另起炉灶，书源界面也不退回私有副本。</para>
    /// </summary>
    internal static class LegadoForm
    {
        /// <summary>分组标题（半粗体，与上一组留间距）。</summary>
        public static TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
        };

        /// <summary>
        /// 生成一个文本字段。<paramref name="commit"/> 为 null 时字段只读（用于展示主键等信息）。
        /// </summary>
        public static UIElement Field(
            string label, string? value, Action<string>? commit, bool multi = false, bool tall = false)
        {
            // ⚠ 属性顺序即行为：AcceptsReturn/TextWrapping 必须在 Text 之前设。
            // WinUI 的 TextBox 在 AcceptsReturn=False（默认）时按"单行"规范化 Text：
            // 此时塞入多行文本会被砍在第一个换行符处（之后再打开 AcceptsReturn 也不会还原）。
            // 对象初始化器按书写顺序执行，Text 放前面 = 多行规则串永远只显示第一行。
            var box = new TextBox
            {
                AcceptsReturn = multi,
                TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Text = value ?? string.Empty,
                IsReadOnly = commit is null,
                Height = tall ? 120 : (multi ? 64 : double.NaN),
                IsEnabled = commit is not null,
            };

            // MinHeight 只在多行时给。
            // 刻意不写 `MinHeight = multi ? 56 : double.NaN`：WinUI 的 MinHeight setter
            // 收到 NaN 会抛 ArgumentException（Height 允许 NaN 表示"自动"，Min/Max 不允许），
            // 于是每次构建单行字段都崩 —— 点开任意书源即致命错误。
            if (multi)
            {
                box.MinHeight = 56;
            }

            if (commit is not null)
            {
                box.TextChanged += (s, e) =>
                {
                    var newText = box.Text;
                    commit(newText);
                };
            }

            return Labeled(label, box);
        }

        /// <summary>
        /// 生成一个整数字段。输入非法时保持上一个合法值（不清空、不弹错）——
        /// 与书源管理里"权重"字段的解析策略一致：顺序/层级这类数字输到一半
        /// 不该把表单洗掉。
        /// </summary>
        public static UIElement IntField(string label, int value, Action<int> commit)
        {
            var box = new TextBox
            {
                Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TextWrapping = TextWrapping.NoWrap,
            };

            box.TextChanged += (s, e) =>
            {
                if (int.TryParse(box.Text, out var n))
                {
                    commit(n);
                }
            };

            return Labeled(label, box);
        }

        /// <summary>标签 + 控件的一行单元（所有字段构件共用，保证间距一致）。</summary>
        /// <remarks>
        /// 标签必须 <c>TextWrapping="Wrap"</c>：规则表单的标签带完整语义说明
        /// （如"作用范围（空或 * = 全部书籍；source:书源URL；book:书籍URL；书名;书籍URL）"
        /// 约 630px），单行不换行会在编辑区右缘被硬裁。书源管理的标签都短，不受影响。
        /// </remarks>
        private static UIElement Labeled(string label, UIElement input) => new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                input,
            },
        };

        /// <summary>开关字段（切换即提交，无"保存"按钮语义）。</summary>
        public static UIElement Toggle(string label, bool value, Action<object, bool> onChange)
        {
            var toggle = new ToggleSwitch
            {
                Header = label,
                IsOn = value,
                OnContent = "开",
                OffContent = "关",
            };

            // ToggleSwitch 无 Click 事件，用 Toggled（切换即提交）
            toggle.Toggled += (s, e) => onChange(toggle, toggle.IsOn);
            return toggle;
        }
    }
}

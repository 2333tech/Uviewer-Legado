using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Uviewer.Services.Legado.Models;
using Xunit;
using ReplaceRule = Uviewer.Services.Legado.Models.ReplaceRule;
using TitleRule = Uviewer.Services.Legado.Models.TitleRule;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 规则编辑规格（<see cref="RuleEditSpec"/>）的契约测试。
    /// <para>覆盖三类风险：① 克隆漏字段（编辑副本与存储实例漂移）；
    /// ② 新规则默认值非法（新建即存了一条永不生效的规则）；
    /// ③ 非法输入穿过校验（正则在净化/切分期被静默跳过，用户看到"存了却没效果"）。</para>
    /// </summary>
    public class RuleEditSpecTests
    {
        // ---------- 1. 字段顺序 / 标签 ----------

        [Fact]
        public void FieldOrder_ReplaceRule_IsDeterministicAndCoversAllFormFields()
        {
            var order = RuleEditSpec.FieldOrder(typeof(ReplaceRule));

            // 规范顺序（不含 Id，Id 排最后只读展示）
            Assert.Equal(
                new[] { "Name", "Group", "Pattern", "Replacement", "Scope", "ScopeTitle", "ScopeContent", "IsRegex", "IsEnabled", "Order", "Id" },
                order);

            // 表单可编辑属性一个不漏（漏一个 = 表单少一个字段）
            var formable = typeof(ReplaceRule)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite &&
                            (p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Empty(formable.Except(order));
        }

        [Fact]
        public void FieldOrder_TitleRule_KeepsRuleSpecificFieldsInPlace()
        {
            var order = RuleEditSpec.FieldOrder(typeof(TitleRule));

            Assert.Equal(
                new[] { "Name", "Group", "Pattern", "TitleGroup", "IsVolume", "IsBookTitle", "Level", "IsEnabled", "Order", "Id" },
                order);
        }

        [Fact]
        public void Label_EveryFormFieldHasChineseLabel_AndIdIsLast()
        {
            foreach (var type in new[] { typeof(ReplaceRule), typeof(TitleRule) })
            {
                var order = RuleEditSpec.FieldOrder(type);
                Assert.Equal("Id", order[^1]);
                foreach (var name in order)
                {
                    var label = RuleEditSpec.Label(type, name);
                    Assert.False(string.IsNullOrWhiteSpace(label), $"{type.Name}.{name} 缺少标签");
                    Assert.NotEqual(name, label); // 不允许退回属性名（英文裸字段对用户不友好）
                }
            }
        }

        [Theory]
        [InlineData("Pattern", true)]
        [InlineData("Replacement", true)]
        [InlineData("Scope", true)]
        [InlineData("Group", true)]
        [InlineData("Name", false)]
        [InlineData("IsRegex", false)]
        [InlineData("Order", false)]
        public void IsLongText_MatchesLongFormFields(string prop, bool expected)
        {
            Assert.Equal(expected, RuleEditSpec.IsLongText(prop));
        }

        [Fact]
        public void IsReadOnlyKey_OnlyId()
        {
            Assert.True(RuleEditSpec.IsReadOnlyKey("Id"));
            Assert.False(RuleEditSpec.IsReadOnlyKey("Name"));
            Assert.False(RuleEditSpec.IsReadOnlyKey("Pattern"));
        }

        // ---------- 2. 克隆保真 ----------

        [Fact]
        public void Clone_ReplaceRule_CopiesEveryProperty_AndDetachesFromOriginal()
        {
            var original = new ReplaceRule
            {
                Id = 7,
                Name = "去广告",
                Group = "g1",
                Pattern = @"<div class=""ad"">.*?</div>",
                Replacement = string.Empty,
                Scope = "book:https://example.com/book/1",
                IsEnabled = false,
                IsRegex = true,
                Order = 3,
            };

            var copy = RuleEditSpec.Clone(original);

            // 逐属性核对（反射）：漏拷任何字段都会在此暴露
            foreach (var prop in typeof(ReplaceRule).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.Equal(prop.GetValue(original), prop.GetValue(copy));
            }

            // 副本是独立实例：改副本不反弹回原对象
            copy.Name = "改过了";
            copy.Pattern = "x";
            Assert.Equal("去广告", original.Name);
            Assert.NotSame(original, copy);
        }

        [Fact]
        public void Clone_TitleRule_CopiesEveryProperty_IncludingTitleSpecificFields()
        {
            var original = new TitleRule
            {
                Id = 12,
                Name = "卷标题",
                Group = "vol",
                Pattern = @"^\s*第[一二三四五六七八九十百千]+卷.*$",
                TitleGroup = 1,
                IsVolume = true,
                IsBookTitle = false,
                Level = 2,
                IsEnabled = true,
                Order = 5,
            };

            var copy = RuleEditSpec.Clone(original);

            foreach (var prop in typeof(TitleRule).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.Equal(prop.GetValue(original), prop.GetValue(copy));
            }

            copy.IsVolume = false;
            copy.Level = 1;
            Assert.True(original.IsVolume);
            Assert.Equal(2, original.Level);
        }

        // ---------- 3. 新建默认值 ----------

        [Fact]
        public void NewReplaceRule_HasLegadoUiDefaults_PatternMustBeFilledBeforeSave()
        {
            var rule = RuleEditSpec.NewReplaceRule(existingCount: 4);

            // 对齐 legado UI 新建语义：scope='*' / isEnabled=true / isRegex=false / order=长度+1
            Assert.Equal("*", rule.Scope);
            Assert.True(rule.IsEnabled);
            Assert.False(rule.IsRegex);
            Assert.Equal(5, rule.Order);
            Assert.Equal(0, rule.Id); // 存储层保存时分配

            // 匹配串故意不预填：新建即存 = 存一条永不命中的规则，
            // 由校验拦住并提示（而不是给个"默认匹配串"悄悄替换正文）
            var error = RuleEditSpec.Validate(rule);
            Assert.NotNull(error);
            Assert.Contains("匹配串", error);

            // 填上匹配串后立即可存
            rule.Pattern = "广告";
            Assert.Null(RuleEditSpec.Validate(rule));
        }

        [Fact]
        public void NewTitleRule_PrefillsCommonChinesePattern_AndIsValid()
        {
            var rule = RuleEditSpec.NewTitleRule(existingCount: 0);

            Assert.Equal(1, rule.Level);
            Assert.True(rule.IsEnabled);
            Assert.False(rule.IsVolume);
            Assert.False(rule.IsBookTitle);
            Assert.Equal(0, rule.TitleGroup);
            Assert.Equal(1, rule.Order);
            Assert.Equal(0, rule.Id);

            // 预填正则必须可用（新建即存也合法），且真能匹配常见写法
            Assert.Null(RuleEditSpec.Validate(rule));
            Assert.Matches(new System.Text.RegularExpressions.Regex(rule.Pattern), "第一章 陨落的天才");
            Assert.Matches(new System.Text.RegularExpressions.Regex(rule.Pattern), "第12章  归来");
        }

        // ---------- 4. 保存校验 ----------

        [Fact]
        public void Validate_ReplaceRule_RejectsEmptyPattern()
        {
            var error = RuleEditSpec.Validate(new ReplaceRule { Pattern = "   " });
            Assert.NotNull(error);
            Assert.Contains("匹配串", error);
        }

        [Fact]
        public void Validate_ReplaceRule_RejectsInvalidRegex_OnlyWhenRegexMode()
        {
            // 字面模式："[(" 只是普通文本，合法
            Assert.Null(RuleEditSpec.Validate(new ReplaceRule { Pattern = "[(", IsRegex = false }));

            // 正则模式："[(" 编译必炸，保存前拦住
            var error = RuleEditSpec.Validate(new ReplaceRule { Pattern = "[(", IsRegex = true });
            Assert.NotNull(error);
            Assert.Contains("正则", error);
        }

        [Fact]
        public void Validate_ReplaceRule_AcceptsValidLiteralAndRegex()
        {
            Assert.Null(RuleEditSpec.Validate(new ReplaceRule { Pattern = "广告", Replacement = "" }));
            Assert.Null(RuleEditSpec.Validate(new ReplaceRule { Pattern = @"<script>.*?</script>", IsRegex = true }));
        }

        [Fact]
        public void Validate_TitleRule_EmptyPatternOnlyAllowedForBookTitle()
        {
            // 空正则且非书名行 → 永不命中，拦截（与 TitleRuleManager 入库条件一致）
            var error = RuleEditSpec.Validate(new TitleRule { Pattern = "", IsBookTitle = false });
            Assert.NotNull(error);

            // 书名行规则靠 IsBookTitle 标记工作，允许空正则
            Assert.Null(RuleEditSpec.Validate(new TitleRule { Pattern = "", IsBookTitle = true }));
        }

        [Fact]
        public void Validate_TitleRule_RejectsInvalidRegexAndBadLevel()
        {
            Assert.NotNull(RuleEditSpec.Validate(new TitleRule { Pattern = "([", }));
            Assert.NotNull(RuleEditSpec.Validate(new TitleRule { Pattern = @"^\s*第.+章", Level = 0 }));
            Assert.NotNull(RuleEditSpec.Validate(new TitleRule { Pattern = @"^\s*第.+章", TitleGroup = -1 }));
            Assert.Null(RuleEditSpec.Validate(new TitleRule { Pattern = @"^\s*第.+章", Level = 1 }));
        }

        // ---------- 5. 详情摘要（列表副标题复用服务层同一实现，这里锁住关键片段） ----------

        [Fact]
        public void DetailOf_ReplaceRule_SummarizesKindScopeAndPattern()
        {
            var rule = new ReplaceRule
            {
                Group = "ad",
                IsRegex = true,
                Scope = "*",
                Pattern = new string('x', 60),
            };

            var detail = RuleEditSpec.ReplaceRuleDetail(rule);

            Assert.Contains("[ad]", detail);
            Assert.Contains("正则", detail);
            Assert.Contains("全部书籍", detail);
            Assert.Contains("…", detail); // 超长匹配串截断
        }

        [Fact]
        public void DetailOf_TitleRule_MarksBookTitleAndVolume()
        {
            Assert.Contains("书名行", RuleEditSpec.TitleRuleDetail(new TitleRule { IsBookTitle = true }));
            Assert.Contains("卷标题", RuleEditSpec.TitleRuleDetail(new TitleRule { IsVolume = true, Pattern = "^第.卷" }));
            Assert.Contains("层级 2", RuleEditSpec.TitleRuleDetail(new TitleRule { Pattern = "^第.节", Level = 2 }));
        }

        [Fact]
        public void NameOf_FallsBackForBlankNames()
        {
            Assert.Equal("(未命名规则)", RuleEditSpec.ReplaceRuleName(new ReplaceRule { Name = "  " }));
            Assert.Equal("(未命名规则)", RuleEditSpec.TitleRuleName(new TitleRule { Name = "" }));
            Assert.Equal("有名字", RuleEditSpec.ReplaceRuleName(new ReplaceRule { Name = "有名字" }));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Editor.Help;

namespace YLFramework.Tests.Help
{
    /// <summary>全局与成员搜索的匹配、评分和排序回归测试；不执行示例或运行时 API。</summary>
    public sealed class SearchScoringTests
    {
        private const string ModuleId = "search-scoring-fixture";
        private static readonly Type _fixture = typeof(Fixtures.ApiFixture<>);
        private static readonly Type _otherFixture = typeof(Other.ApiFixture);

        [Test]
        public void ScorePrefersExactThenPrefixThenContainsAcrossFieldWeights()
        {
            var terms = HelpSearch.Tokenize("sequence");
            Assert.That(HelpSearch.Score(Fields(("Sequence", HelpSearch.TitleWeight)), terms), Is.EqualTo(130));
            Assert.That(HelpSearch.Score(Fields(("SequenceAction", HelpSearch.TitleWeight)), terms), Is.EqualTo(120));
            Assert.That(HelpSearch.Score(Fields(("包含 sequence 的摘要", HelpSearch.SummaryWeight)), terms), Is.EqualTo(90));
            Assert.That(HelpSearch.Score(Fields(("Run Sequence Now", HelpSearch.BodyWeight)), terms), Is.EqualTo(80));
            Assert.That(HelpSearch.Score(Fields(("public void Run();", HelpSearch.TechnicalWeight)), terms), Is.EqualTo(-1));
        }

        [Test]
        public void TokenizeTrimsCollapsesAndDeduplicatesCaseInsensitive()
        {
            CollectionAssert.AreEqual(new[] { "Run", "取消" }, HelpSearch.Tokenize("  Run \t run   RUN 取消 "));
            Assert.That(HelpSearch.Tokenize("   "), Is.Empty);
        }

        [Test]
        public void ScoreRequiresEveryTermAndAllowsTermsAcrossDifferentFields()
        {
            var fields = Fields(("Run", HelpSearch.TitleWeight), ("运行入口", HelpSearch.SummaryWeight));
            Assert.That(HelpSearch.Score(fields, HelpSearch.Tokenize("Run 运行")), Is.GreaterThan(0));
            Assert.That(HelpSearch.Score(fields, HelpSearch.Tokenize("Run 未出现")), Is.EqualTo(-1));
        }

        [Test]
        public void DotQualifiedIdentifiersSplitIntoAndPartsWithoutLooseOrResults()
        {
            var type = HelpApiIndex.Build(Module(_fixture)).Single();
            var memberFields = new List<HelpSearchField>();
            type.Entries.Single(entry => entry.Name == "Run").AppendSearchFields(memberFields, type.Name);
            Assert.That(HelpSearch.Score(memberFields, HelpSearch.Tokenize("ApiFixture.Run")), Is.GreaterThan(0));
            Assert.That(HelpSearch.Score(memberFields, HelpSearch.Tokenize("ApiFixture.不存在")), Is.EqualTo(-1));
            var context = new List<HelpSearchField>();
            type.AppendSearchFields(context, false, false);
            Assert.That(HelpSearch.Score(context, HelpSearch.Tokenize("ApiFixture.Run")), Is.LessThan(0),
                "类型身份字段不覆盖成员名，成员级结果需要单独列出");
        }

        [Test]
        public void MemberNameOutweighsSummaryAndParameterDocumentation()
        {
            var module = Module(_fixture);
            module.Documentation = member => member.Name == "Run"
                ? new HelpDocumentation("运行入口", null, "备注说明", new[] { new HelpParameter("value", "数值") })
                : null;
            var type = HelpApiIndex.Build(module).Single();
            var fields = new List<HelpSearchField>();
            type.Entries.Single(entry => entry.Name == "Run").AppendSearchFields(fields, type.Name);
            var byName = HelpSearch.Score(fields, HelpSearch.Tokenize("run"));
            var bySummary = HelpSearch.Score(fields, HelpSearch.Tokenize("运行入口"));
            var byParameter = HelpSearch.Score(fields, HelpSearch.Tokenize("数值"));
            Assert.That(byName, Is.GreaterThan(bySummary));
            Assert.That(bySummary, Is.GreaterThan(byParameter));
        }

        [Test]
        public void WindowRanksExactTitleBeforePrefixAndBodyAndKeepsAndSemantics()
        {
            var saved = ReplaceModules(new HelpModule
            {
                Guides = new[]
                {
                    new HelpPage("exact", "Sequence", "动作", "精确标题页面"),
                    new HelpPage("prefix", "Sequence 使用指南", "动作", ""),
                    new HelpPage("body", "延迟详解", "动作", "正文里出现的 sequence 词")
                }
            });
            var window = ScriptableObject.CreateInstance<HelpWindow>();
            try
            {
                window.SelectModule(ModuleId);
                Set(window, "search", "sequence");
                UpdateResults(window);
                Assert.That(Ids(window), Is.EqualTo(new[] { "exact", "prefix", "body" }), "大小写不敏感且精确标题优先");
                Set(window, "search", "Sequence 使用");
                UpdateResults(window);
                Assert.That(Ids(window), Is.EqualTo(new[] { "prefix" }), "多词按 AND 匹配且可命中不同字段");
                Set(window, "search", "延迟 sequence");
                UpdateResults(window);
                Assert.That(Ids(window), Is.EqualTo(new[] { "body" }), "中英文混合查询");
                Set(window, "search", "不存在的关键词");
                UpdateResults(window);
                Assert.That(Ids(window), Is.Empty);
                Set(window, "search", "");
                UpdateResults(window);
                Assert.That(Ids(window).Length, Is.EqualTo(3), "清空搜索后恢复正常导航");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                RestoreModules(saved);
            }
        }

        [Test]
        public void WindowListsMatchedMembersButNotForPureTypeQueries()
        {
            var saved = ReplaceModules(Module(_fixture, _otherFixture));
            var window = ScriptableObject.CreateInstance<HelpWindow>();
            try
            {
                window.SelectModule(ModuleId);
                Set(window, "search", "ApiFixture");
                UpdateResults(window);
                var types = Get<HelpApiType[]>(window, "_visibleTypes");
                Assert.That(types[0].Name, Is.EqualTo("ApiFixture"), "精确类型名排在仅前缀命中之前");
                Assert.That(Get<Dictionary<string, HelpApiMember[]>>(window, "_visibleMembers"), Is.Empty,
                    "纯类型名查询不列出全部成员");
                Set(window, "search", "ApiFixture Run");
                UpdateResults(window);
                var fixture = Get<HelpApiType[]>(window, "_visibleTypes").Single(type => type.Type == _fixture);
                var members = Get<Dictionary<string, HelpApiMember[]>>(window, "_visibleMembers");
                Assert.That(members.ContainsKey(fixture.Id), Is.True, "成员名命中时应列出成员级结果");
                Assert.That(members[fixture.Id].Any(entry => entry.Name == "Run"), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                RestoreModules(saved);
            }
        }

        private static List<HelpSearchField> Fields(params (string Text, int Weight)[] values) =>
            values.Select(value => new HelpSearchField(value.Text, value.Weight)).ToList();

        private static HelpModule Module(params Type[] types) => new HelpModule
        {
            ApiTypes = types.Select(type => new HelpApiTypeDefinition(type, "测试 API")).ToArray(),
            XmlPaths = types.Select(type => type.Assembly).Distinct().ToDictionary(assembly => assembly,
                assembly => Path.Combine(Path.GetTempPath(), "missing-search-scoring-" + assembly.GetName().Name + ".xml"))
        };

        private static HelpRegistration[] ReplaceModules(HelpModule module)
        {
            var saved = HelpRegistry.GetModules();
            foreach (var entry in saved)
            {
                HelpRegistry.Unregister(entry.Id);
            }

            HelpRegistry.Register(ModuleId, "搜索评分测试", () => module);
            return saved;
        }

        private static void RestoreModules(HelpRegistration[] saved)
        {
            HelpRegistry.Unregister(ModuleId);
            foreach (var entry in saved)
            {
                HelpRegistry.Register(entry.Id, entry.DisplayName, entry.Create);
            }
        }

        private static string[] Ids(HelpWindow window) =>
            Get<HelpPage[]>(window, "_visibleTopics").Select(page => page.Id).ToArray();

        private static T Get<T>(object instance, string name) => (T)instance.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);

        private static void Set(HelpWindow window, string name, object value) => typeof(HelpWindow)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, value);

        private static void UpdateResults(HelpWindow window) => typeof(HelpWindow)
            .GetMethod("UpdateResults", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
    }
}

using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    public class RuleCatalogTests
    {
        [Test]
        public void ExistingRuleCatalogPreservesIdsCategoriesAndDefaults()
        {
            var rules = new CoreUsageAnalyzer().SupportedDiagnostics;
            Assert.That(rules.Take(7).Select(rule => rule.Id), Is.EqualTo(new[]
            {
                "YL0001", "YL0002", "YL0003", "YL0004", "YL0005", "YL0006", "YL0007"
            }));
            foreach (var rule in rules)
            {
                Assert.That(rule.DefaultSeverity, Is.EqualTo(DiagnosticSeverity.Warning), rule.Id);
                Assert.That(rule.IsEnabledByDefault, Is.True, rule.Id);
                Assert.That(rule.Title.ToString(), Is.Not.Empty, rule.Id);
                Assert.That(rule.Description.ToString(), Does.Match(@"[\u4e00-\u9fff]"), rule.Id);
            }
            Assert.That(rules.Take(6).All(rule => rule.Category == "YLFramework.Usage"), Is.True);
            Assert.That(rules[6].Category, Is.EqualTo("YLFramework.Lifecycle"));
        }

        [Test]
        public void AnalyzerDoesNotDependOnUnityEditorOrWorkspaces()
        {
            var references = typeof(CoreUsageAnalyzer).Assembly.GetReferencedAssemblies().Select(name => name.Name);
            Assert.That(references.Any(name => name.StartsWith("Unity", StringComparison.Ordinal) ||
                name.StartsWith("YLFramework", StringComparison.Ordinal) ||
                name.Contains("Workspaces")), Is.False);
        }

        [Test]
        public void EverySupportedRuleHasALocalDocumentationAnchor()
        {
            var guide = File.ReadAllText(Path.Combine(AnalyzerFixture.Root,
                "PackageSources/com.kano.ylframework/Documentation~/Modules/Attributes.md"));
            foreach (var rule in new CoreUsageAnalyzer().SupportedDiagnostics)
            {
                Assert.That(guide.Contains("id=\"" + rule.Id.ToLowerInvariant() + "\""), Is.True,
                    rule.Id + " 缺少本地文档锚点");
            }
        }
    }
}

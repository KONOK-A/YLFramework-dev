using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Core.Analyzers;

namespace YLFramework.Core.Editor.Tests
{
    public class AnalyzerRuleSettingsTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        [Test]
        public void NativeProviderRegistersTheOwnedRouteAndTenSearchableRulesWithoutWriting()
        {
            var files = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Assets"), "*.ruleset", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty).ToArray();
            var type = ProviderType();
            var factory = type.GetMethods(Members).Single(method => method.IsDefined(typeof(SettingsProviderAttribute), false));
            var provider = (SettingsProvider)Invoke(factory);
            Assert.That(provider.settingsPath, Is.EqualTo("Project/YLFramework/Analyzers"));
            Assert.That(provider.scope, Is.EqualTo(SettingsScope.Project));
            Assert.That(provider.keywords, Does.Contain("YL0010"));
            Assert.That(AnalyzerRuleCatalog.Rules.Select(rule => rule.Id), Is.EqualTo(new[]
            {
                "YL0001", "YL0002", "YL0003", "YL0004", "YL0005", "YL0006", "YL0007", "YL0008", "YL0009", "YL0010"
            }));
            var menus = TypeCache.GetMethodsWithAttribute<MenuItem>().SelectMany(method => method.GetCustomAttributes<MenuItem>())
                .Where(menu => menu.menuItem == "YLFramework/Core/分析器规则" && !menu.validate).ToArray();
            Assert.That(menus.Length, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Assets"), "*.ruleset", SearchOption.AllDirectories).OrderBy(path => path), Is.EqualTo(files));
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty), Is.EqualTo(scenes));
        }

        [TestCase(0, true, true, true, true, false, false, "未找到")]
        [TestCase(2, true, true, true, true, false, false, "多份")]
        [TestCase(1, false, true, true, true, false, false, "RoslynAnalyzer")]
        [TestCase(1, null, true, true, true, false, false, "RoslynAnalyzer")]
        [TestCase(1, true, false, true, true, false, false, "平台")]
        [TestCase(1, true, null, true, true, false, false, "平台")]
        [TestCase(1, true, true, false, true, false, false, "Runtime")]
        [TestCase(1, true, true, true, false, false, false, "编译图")]
        [TestCase(1, true, true, true, null, false, false, "编译图")]
        [TestCase(1, true, true, true, true, true, false, "等待编译")]
        [TestCase(1, true, true, true, true, false, true, "编译失败")]
        [TestCase(1, true, true, true, true, false, false, "结构检查通过")]
        public void ConnectionStateNeverClaimsThatSavingOrZeroDiagnosticsProvesExecution(int count, bool? label,
            bool? platforms, bool? explicitReference, bool? included, bool compiling, bool failed, string expected)
        {
            var text = (string)Invoke(ProviderType().GetMethod("DescribeConnection", Members),
                count, label, platforms, explicitReference, included, compiling, failed);
            Assert.That(text, Does.Contain(expected));
            Assert.That(text, Does.Contain("未核实"));
            Assert.That(text, Does.Not.Contain("分析已生效"));
        }

        [TestCase("Assembly-CSharp", null, true, "Assets/Games/Feature.cs", true)]
        [TestCase("Game.Feature", "Assets/Games/Feature/Feature.asmdef", true, "Assets/Games/Feature/Feature.cs", true)]
        [TestCase("UnknownDefault", null, true, "Assets/Games/Feature.cs", false)]
        [TestCase("YLFramework.Runtime", "Packages/com.kano.ylframework/Runtime/YLFramework.Runtime.asmdef", true, "Packages/com.kano.ylframework/Runtime/Core/Module.cs", false)]
        [TestCase("Package.Consumer", "Packages/Other/Other.asmdef", true, "Packages/Other/Other.cs", false)]
        [TestCase("Unrelated", "Assets/Other/Other.asmdef", false, "Assets/Other/Other.cs", false)]
        public void ConfigurationScopeDistinguishesBusinessFromFrameworkPackagesAndUnrelatedAssemblies(
            string name, string definition, bool referencesRuntime, string source, bool writable)
        {
            var root = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp/AnalyzerSettingsScope"));
            var reason = (string)Invoke(ProviderType().GetMethod("ConfigurationBlock", Members),
                root, name, definition, referencesRuntime, new[] { source });
            Assert.That(reason == null, Is.EqualTo(writable), reason);
        }

        [UnityTest]
        public IEnumerator RealProviderRendersLiveConnectionWithoutCreatingRuleSetsOrDirtyingScenes()
        {
            var files = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Assets"), "*.ruleset", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty).ToArray();
            var focus = EditorWindow.focusedWindow;
            var provider = AnalyzerRuleSettingsProvider.CreateProvider();
            var window = ScriptableObject.CreateInstance<AnalyzerSettingsTestWindow>();
            try
            {
                window.position = new Rect(100, 100, 900, 700);
                window.Attach(provider);
                window.ShowUtility();
                window.Repaint();
                for (var frame = 0; frame < 60 && !window.Rendered; frame++)
                {
                    yield return null;
                }
                Assert.That(window.Rendered, Is.True);
                Assert.That(provider.GetType().GetField("_dllCount", Members).GetValue(provider), Is.EqualTo(1));
                Assert.That(provider.GetType().GetField("_readError", Members).GetValue(provider), Is.Null);
                Assert.That(provider.GetType().GetField("_dllDetails", Members).GetValue(provider),
                    Does.Contain("Version:").And.Contain("SHA256:"));
                foreach (var field in new[] { "_hasLabel", "_platformsDisabled", "_explicitReference", "_included" })
                {
                    Assert.That(provider.GetType().GetField(field, Members).GetValue(provider), Is.EqualTo(true), field);
                }
                Assert.That(provider.GetType().GetField("_preview", Members).GetValue(provider), Is.Null);
                Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Assets"), "*.ruleset", SearchOption.AllDirectories).OrderBy(path => path), Is.EqualTo(files));
                Assert.That(Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty), Is.EqualTo(scenes));
            }
            finally
            {
                window.Close();
                provider.OnDeactivate();
                if (focus != null)
                {
                    focus.Focus();
                }
            }
        }

        private static Type ProviderType()
        {
            var type = System.Reflection.Assembly.Load("YLFramework.Editor").GetType(
                "YLFramework.Editor.Core.Analyzers.AnalyzerRuleSettingsProvider");
            Assert.That(type, Is.Not.Null, "分析器规则设置页尚未实现");
            return type;
        }

        private static object Invoke(MethodInfo method, params object[] arguments)
        {
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    internal sealed class AnalyzerSettingsTestWindow : EditorWindow
    {
        private SettingsProvider _provider;
        internal bool Rendered
        {
            get; private set;
        }

        internal void Attach(SettingsProvider provider)
        {
            _provider = provider;
            _provider.OnActivate("", null);
        }

        private void OnGUI()
        {
            _provider?.OnGUI("");
            if (Event.current.type == EventType.Repaint)
            {
                Rendered = true;
            }
        }

        private void OnDisable() => _provider?.OnDeactivate();
    }
}

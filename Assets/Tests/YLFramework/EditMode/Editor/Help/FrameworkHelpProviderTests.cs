using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Help;

namespace YLFramework.Tests.Help
{
    public class FrameworkHelpProviderTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void DeliveryGuidesDescribeUpmInsteadOfTheRetiredExporterAndUseCurrentOutputPaths()
        {
            var tools = Registration("foundation-tools").Create();
            var delivery = tools.Guides.Single(page => page.Id == "tools").SearchText;
            StringAssert.Contains("UPM", delivery);
            StringAssert.DoesNotContain("导出框架包", delivery);
            StringAssert.DoesNotContain("默认导出", delivery);
            Assert.That(tools.Guides.Any(page => page.SearchText.Contains("Documentation~/Modules")), Is.True);
            var authoring = Registration("core").Create().Guides.Single(page => page.Id == "authoring").SearchText;
            StringAssert.Contains("Assets/YLFrameworkData/Generated/ResourceReferences", authoring);
            StringAssert.Contains("不随 UPM 包交付", authoring);
            StringAssert.DoesNotContain("默认导出范围", authoring);
        }

        [Test]
        public void FoundationAndToolsKeepTheirCuratedNavigationGroupsAfterAssemblyMerge()
        {
            var module = Registration("foundation-tools").Create();
            var index = HelpApiIndex.Build(module);
            foreach (var guide in module.Guides)
            {
                foreach (var type in guide.FeaturedTypes)
                {
                    Assert.That(index.Single(entry => entry.Type == type).Group,
                        Is.EqualTo(guide.Id == "tools" ? "Tools 精选 API" : "Foundation 精选 API"), type.FullName);
                }
            }

            Assert.That(index.Select(entry => entry.Group).Distinct(),
                Is.EquivalentTo(new[] { "Tools 精选 API", "Foundation 精选 API" }));
            foreach (var type in new[] { typeof(YLFramework.UI.ColorSelector), typeof(YLFramework.UI.ColorSelectorWindow) })
            {
                Assert.That(module.Guides.Single(guide => guide.Id == "tools").FeaturedTypes, Does.Contain(type), type.FullName);
            }
        }

        [TestCase("core", "YLFramework.Editor", "YLFramework/Core/帮助")]
        [TestCase("foundation-tools", "YLFramework.Editor", "YLFramework/Toolkit/帮助")]
        public void RegisteredProviderOffersOnlyCuratedOwnedApi(string id, string assemblyName, string menu)
        {
            var registration = Registration(id);
            var before = ReadOnlyState();
            var module = registration.Create();
            var index = HelpApiIndex.Build(module);
            Assert.That(ReadOnlyState(), Is.EqualTo(before), "Reading help must not initialize a host or write assets.");
            Assert.That(module.Guides, Is.Not.Empty);
            Assert.That(module.Guides.Select(p => p.Id).Distinct().Count(), Is.EqualTo(module.Guides.Length));
            Assert.That(index, Is.Not.Empty);
            Assert.That(index.Select(t => t.Type).Distinct().Count(), Is.EqualTo(index.Length));
            foreach (var page in module.Guides)
            {
                Assert.That(page.FeaturedTypes, Is.Not.Empty, page.Id);
                foreach (var type in page.FeaturedTypes)
                {
                    Assert.That(page.RelatedTypes, Does.Contain(type), page.Id);
                    Assert.That(index.Single(t => t.Type == type).RelatedPages, Does.Contain(page));
                }
            }
            var allowed = new[] { "YLFramework.Runtime" };
            Assert.That(index.All(t => allowed.Contains(t.Type.Assembly.GetName().Name)), Is.True);
            Assert.That(index.Any(t => t.Type.Namespace != null &&
                (t.Type.Namespace.StartsWith("YLFramework.Audio") || t.Type.Namespace.StartsWith("YLFramework.Actions"))), Is.False);
            var entry = TypeCache.GetMethodsWithAttribute<MenuItem>().Single(m =>
                m.GetCustomAttributes<MenuItem>().Any(a => a.menuItem == menu && !a.validate));
            Assert.That(entry.DeclaringType.Assembly.GetName().Name, Is.EqualTo(assemblyName));
            Assert.That(entry.DeclaringType.IsDefined(typeof(InitializeOnLoadAttribute), false), Is.True);
            Assert.That(HelpRegistry.GetModules().Count(m => m.Id == "actionkit"), Is.EqualTo(1));
            Assert.That(HelpRegistry.GetModules().Count(m => m.Id == "audiokit"), Is.EqualTo(1));
        }

        [TestCase("core", "View", "RegisterEvent")]
        [TestCase("foundation-tools", "EventBus", "Register")]
        public void MissingXmlKeepsDeclarationsAndSearchableMembers(string id, string typeName, string memberName)
        {
            var module = Registration(id).Create();
            // 只改本次工厂返回的数据，不删除或改写工程已有 XML。
            foreach (var assembly in module.ApiTypes.Select(t => t.Type.Assembly).Distinct())
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(assembly.Location, ".xml"))), Is.False,
                    "This fixture requires no adjacent XML; do not remove an existing document to run it.");
                module.XmlPaths[assembly] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            }
            var index = HelpApiIndex.Build(module);
            var type = index.Single(t => t.Type.Name == typeName);
            Assert.That(type.Declaration, Does.Contain(typeName));
            Assert.That(type.Entries.Any(e => e.Name == memberName && e.Signature.Contains(memberName)), Is.True);
            Assert.That(type.Matches(typeName + " " + memberName), Is.True);
            Assert.That(type.Entries.Where(e => e.Name == memberName).All(e => string.IsNullOrEmpty(e.Help.Summary)), Is.True,
                "Missing XML must not manufacture member documentation.");
        }

        [Test]
        public void GuidesMakeAnalyzerAndAttributeBoundariesDiscoverable()
        {
            var core = Registration("core").Create();
            foreach (var query in new[] { "Module MVC", "View OnLoaded", "PanelRule UIRootContext",
                "Assets/Create/YLFramework/Core/Module", "YLFramework.Generated.ResourceNames" })
            {
                Assert.That(core.Guides.Any(p => p.Matches(query)), Is.True, query);
            }

            for (var id = 1; id <= 10; id++)
            {
                Assert.That(core.Guides.Any(p => p.Matches("YL" + id.ToString("D4") + " Warning")), Is.True);
            }

            foreach (var query in new[] { "RoslynAnalyzer YLFramework.Runtime", "ruleset editorconfig", "build.ps1 -Deploy", "SDK 9.0.312",
                "Project Settings Analyzers", "YLFramework/Core/分析器规则", "HasStarted IsDone" })
            {
                Assert.That(core.Guides.Any(p => p.Matches(query)), Is.True, query);
            }

            var tools = Registration("foundation-tools").Create();
            foreach (var query in new[] { "ObjectPool", "EventBus", "Timer", "FSM", "ZipTool",
                "CustomClassNameGui 元数据", "CustomPropertyGui 内置", "DisableEdit Drawer" })
            {
                Assert.That(tools.Guides.Any(p => p.Matches(query)), Is.True, query);
            }
        }

        [UnityTest]
        public IEnumerator RealWindowReadsBothProvidersAndRefreshesDuplicateRegistrationWithoutSideEffects()
        {
            var registrations = new[] { Registration("core"), Registration("foundation-tools") };
            var before = ReadOnlyState();
            var focus = EditorWindow.focusedWindow;
            var subscriptions = RegistrySubscribers();
            var window = ScriptableObject.CreateInstance<HelpWindow>();
            var temporaryId = "__framework_help_refresh_" + Guid.NewGuid().ToString("N");
            try
            {
                window.position = new Rect(100, 100, 900, 700);
                window.ShowUtility();
                foreach (var registration in registrations)
                {
                    window.SelectModule(registration.Id);
                    var query = registration.Id == "core" ? "YL0007" : "CustomPropertyGui";
                    Set(window, "search", query);
                    window.Repaint();
                    for (var frame = 0; frame < 60 && (Get<bool>(window, "_needsReload") ||
                        Get<HelpPage[]>(window, "_visibleTopics").All(p => !p.Matches(query))); frame++)
                    {
                        yield return null;
                    }

                    Assert.That(Get<string>(window, "moduleId"), Is.EqualTo(registration.Id));
                    Assert.That(Get<HelpPage[]>(window, "_visibleTopics").Any(p => p.Matches(query)), Is.True);
                    var highlights = Get<HelpSearchHighlight>(window, "_highlights");
                    var ranges = Get<System.Collections.Generic.Dictionary<string, HelpSearchHighlight.Range[]>>(highlights, "_ranges");
                    Assert.That(ranges.Keys.Any(text => text.Contains(query)), Is.True, "The actual reader must render the guide.");
                    Click(window, new HelpHeaderLayout(window.position.width).Clear.center);
                    Click(window, new HelpHeaderLayout(window.position.width).Tab(1).center);
                    Assert.That(Get<int>(window, "tab"), Is.EqualTo(1));
                    Set(window, "search", registration.Id == "core" ? "View.RegisterEvent" : "EventBus.Register");
                    Set(window, "_filterKey", null);
                    window.Repaint();
                    yield return null;
                    yield return null;
                    Assert.That(Get<HelpApiType[]>(window, "_visibleTypes"), Is.Not.Empty);
                    window.SelectModule(registration.Id == "core" ? "foundation-tools" : "core");
                    Assert.That(Get<string>(window, "search"), Is.Empty);
                }

                // 临时 id 验证替换，不改正式工厂，也不重置用户已打开窗口的阅读状态。
                var firstCalls = 0;
                var intermediateCalls = 0;
                var replacementCalls = 0;
                var replacement = new HelpModule
                {
                    Guides = new[] { new HelpPage("replacement", "替换后的指南", "临时验证", "replacement-v2") }
                };
                var moduleCount = HelpRegistry.GetModules().Length;
                HelpRegistry.Register(temporaryId, "临时帮助", () =>
                {
                    firstCalls++;
                    return registrations[0].Create();
                });
                Assert.That(firstCalls, Is.Zero, "Registration is lazy.");
                window.SelectModule(temporaryId);
                Assert.That(firstCalls, Is.EqualTo(1));
                HelpRegistry.Register(temporaryId, "中间版本", () =>
                {
                    intermediateCalls++;
                    return registrations[1].Create();
                });
                HelpRegistry.Register(temporaryId, "替换版本", () =>
                {
                    replacementCalls++;
                    return replacement;
                });
                Assert.That(replacementCalls, Is.Zero);
                Assert.That(HelpRegistry.GetModules().Length, Is.EqualTo(moduleCount + 1));
                window.Repaint();
                for (var frame = 0; frame < 60 && (replacementCalls == 0 || Get<bool>(window, "_needsReload")); frame++)
                {
                    yield return null;
                }

                Assert.That(Get<bool>(window, "_needsReload"), Is.False);
                Assert.That(firstCalls, Is.EqualTo(1));
                Assert.That(intermediateCalls, Is.Zero);
                Assert.That(replacementCalls, Is.EqualTo(1), "The Changed event must load the newest factory, not cached content.");
                Assert.That(Get<HelpModule>(window, "_module"), Is.SameAs(replacement));
                Assert.That(Get<HelpPage[]>(window, "_visibleTopics").Single().Id, Is.EqualTo("replacement"));
                Assert.That(window.titleContent.text, Is.EqualTo("替换版本 帮助"));
            }
            finally
            {
                window.Close();
                HelpRegistry.Unregister(temporaryId);
                if (focus)
                {
                    focus.Focus();
                }
            }
            Assert.That(RegistrySubscribers(), Is.EqualTo(subscriptions), "Closed windows must release their registry subscription.");
            Assert.That(ReadOnlyState(), Is.EqualTo(before));
        }

        private static HelpRegistration Registration(string id)
        {
            var matches = HelpRegistry.GetModules().Where(m => m.Id == id).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1), "Missing or duplicate formal provider: " + id);
            return matches[0];
        }

        private static string ReadOnlyState()
        {
            var core = typeof(YLFramework.Core.Module).Assembly;
            var manager = core.GetType("YLFramework.Core.ModuleManager").GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            YLFramework.Events.EventBus.TryGetDefault(out var bus);
            var scenes = Enumerable.Range(0, EditorSceneManager.sceneCount).Select(i =>
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                return scene.path + ":" + scene.isDirty + ":" + scene.rootCount;
            });
            var objects = Resources.FindObjectsOfTypeAll<GameObject>().Where(o => !EditorUtility.IsPersistent(o))
                .Select(o => o.GetInstanceID()).OrderBy(i => i);
            var files = new[] { "Assets/YLFrameworkData", "Assets/YLFramework/Examples", "ProjectSettings/YLFramework" }
                .Where(Directory.Exists).SelectMany(p => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(p), "*", SearchOption.AllDirectories))
                .OrderBy(p => p).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks);
            return (manager == null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(manager).ToString()) + "|" +
                (bus == null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bus).ToString()) + "|" +
                string.Join(",", scenes) + "|" + string.Join(",", objects) + "|" + string.Join(",", files) + "|" +
                string.Join(",", HelpRegistry.GetModules().Select(r => r.Id + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r)));
        }

        private static int RegistrySubscribers() => ((Delegate)typeof(HelpRegistry)
            .GetField("Changed", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))?.GetInvocationList().Length ?? 0;
        private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance).GetValue(instance);
        private static void Set(HelpWindow window, string name, object value) => typeof(HelpWindow).GetField(name, PrivateInstance).SetValue(window, value);
        private static void Click(EditorWindow window, Vector2 point)
        {
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = point });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = point });
        }
    }
}

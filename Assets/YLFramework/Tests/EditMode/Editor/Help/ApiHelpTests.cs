using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Editor.Actions;
using YLFramework.Editor.Help;

namespace YLFramework.Tests.Actions
{
    public class ApiHelpTests
    {
        private class ProtectedSetterExample
        {
            public int Value
            {
                get; protected set;
            }
        }
        // Independently mapped from the Runtime and Observable public API inventory.
        private static readonly string[] _expectedCoreTypeNames =
        {
            "YLFramework.Actions.AbstractAction`1", "YLFramework.Actions.ActionController", "YLFramework.Actions.ActionHandle",
            "YLFramework.Actions.ActionKit", "YLFramework.Actions.ActionKit+ScreenTransition", "YLFramework.Actions.ActionKitCurrentScene",
            "YLFramework.Actions.ActionStatus", "YLFramework.Actions.ActionUpdateModes", "YLFramework.Actions.AudioSourceShortCutExtensions",
            "YLFramework.Actions.CallbackExtension", "YLFramework.Actions.ConditionAction", "YLFramework.Actions.ConditionExtension",
            "YLFramework.Actions.ConditionSequence", "YLFramework.Actions.ConditionSequenceExtension", "YLFramework.Actions.CoroutineExtension",
            "YLFramework.Actions.CustomExtension", "YLFramework.Actions.DelayExtension",
            "YLFramework.Actions.DelayFrameExtension",
            "YLFramework.Actions.EaseType", "YLFramework.Actions.EaseUtility",
            "YLFramework.Actions.IAction", "YLFramework.Actions.IActionController", "YLFramework.Actions.IActionControllerExtensions",
            "YLFramework.Actions.IActionExecutor", "YLFramework.Actions.IActionExecutorExtensions",
            "YLFramework.Actions.IActionExtensions", "YLFramework.Actions.IAction`1", "YLFramework.Actions.ICustomApi`1",
            "YLFramework.Actions.IParallel", "YLFramework.Actions.IRepeat",
            "YLFramework.Actions.ISequence", "YLFramework.Actions.Lerp", "YLFramework.Actions.LerpExtension",
            "YLFramework.Actions.MonoUpdateActionExecutorExtension",
            "YLFramework.Actions.ParallelExtension", "YLFramework.Actions.Repeat", "YLFramework.Actions.RepeatExtension",
            "YLFramework.Actions.ScreenTransitionFade", "YLFramework.Actions.ScreenTransitionInOut`2", "YLFramework.Actions.SequenceExtension",
            "YLFramework.Actions.TaskAction", "YLFramework.Actions.TaskExtension",
            "YLFramework.Actions.ActionKitUniRxExtension",
            "YLFramework.Actions.UniRxAction`1"
        };

        [Test]
        public void IndexContainsEveryExportedApiTypeExactlyOnceAndRetainsAllCoreTypes()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            var core = _expectedCoreTypeNames.Select(name => typeof(ActionKit).Assembly.GetType(name, true)).ToArray();
            var optional = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween");
            var expected = core.Concat(optional == null ? Array.Empty<Type>() : new[]
            {
                optional.GetType("YLFramework.Integrations.DOTween.DOTweenAction", true),
                optional.GetType("YLFramework.Integrations.DOTween.ActionKitDOTweenExtension", true)
            }).ToArray();
            CollectionAssert.AreEquivalent(expected, index.Select(entry => entry.Type));
            Assert.That(core.Length, Is.EqualTo(44));
            CollectionAssert.IsSubsetOf(core, index.Select(entry => entry.Type).ToArray());
            Assert.That(index.Select(entry => entry.Type).Distinct().Count(), Is.EqualTo(index.Length));
            Assert.That(index.All(entry => entry.Type.IsVisible), Is.True);
            Assert.That(index.All(entry => entry.Type.Assembly == typeof(ActionKit).Assembly ||
                entry.Type.Assembly.GetName().Name == "YLFramework.Actions.DOTween"), Is.True);
            Assert.That(core.Sum(type => ActionKitApiHelpContent.GetDeclaredApi(type).Count()), Is.EqualTo(303));
            var providerCount = ActionKitApiHelpContent.ApiAssemblies().Count() - 1;
            Assert.That(index.Length, Is.EqualTo(44 + 2 * providerCount));
            Assert.That(index.Sum(entry => entry.Members.Length), Is.EqualTo(303 + 12 * providerCount));
            Assert.That(typeof(ActionKit).Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(index.All(entry => entry.Type.Namespace == "YLFramework.Actions" ||
                entry.Type.Namespace.StartsWith("YLFramework.Actions.", StringComparison.Ordinal) ||
                entry.Type.FullName is "YLFramework.Integrations.DOTween.DOTweenAction" or
                    "YLFramework.Integrations.DOTween.ActionKitDOTweenExtension"), Is.True);
        }

        [Test]
        public void ApiAssembliesOnlyIncludeCoreAndTheExactlyNamedLoadedOptionalProviders()
        {
            var optional = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween").ToArray();
            var assemblies = ActionKitApiHelpContent.ApiAssemblies().ToArray();
            CollectionAssert.AreEquivalent(new[] { typeof(ActionKit).Assembly }.Concat(optional), assemblies);
            Assert.That(assemblies.Count(assembly => assembly == typeof(ActionKit).Assembly), Is.EqualTo(1));
            Assert.That(assemblies.All(assembly => new[] { "YLFramework.Runtime", "YLFramework.Actions.DOTween" }
                .Contains(assembly.GetName().Name)), Is.True);
            var expectedReferences = assemblies.Select(assembly => assembly.Location).ToList();
            var wrapper = optional.SingleOrDefault(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween");
            if (wrapper != null)
            {
                var dependency = wrapper.GetReferencedAssemblies().Single(assembly => assembly.Name == "DOTween");
                var dotween = AppDomain.CurrentDomain.GetAssemblies().Single(assembly =>
                    assembly.GetName().Name == "DOTween" && assembly.GetName().FullName == dependency.FullName);
                Assert.That(dotween.GetType("DG.Tweening.Tween", true).Assembly, Is.SameAs(dotween));
                expectedReferences.Add(dotween.Location);
                CollectionAssert.DoesNotContain(assemblies, dotween, "Vendor API must not enter the ActionKit index.");
            }
            CollectionAssert.AreEquivalent(expectedReferences.Distinct(), ActionKitApiHelpContent.GetReferenceAssemblies());
            if (wrapper == null)
            {
                Assert.That(ActionKitApiHelpContent.GetReferenceAssemblies().Any(path => Path.GetFileName(path) == "DOTween.dll"), Is.False);
            }

            foreach (var name in new[] { "YLFramework.Actions.DOTween", "DOTween" })
            {
                Assert.That(typeof(ActionKitApiHelpContent).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == name), Is.False);
                Assert.That(typeof(ApiHelpTests).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == name), Is.False);
            }
            foreach (var assembly in optional)
            {
                var names = new[] { "YLFramework.Integrations.DOTween.DOTweenAction", "YLFramework.Integrations.DOTween.ActionKitDOTweenExtension" };
                CollectionAssert.AreEquivalent(names,
                    assembly.GetExportedTypes().Select(type => type.FullName));
                Assert.That(assembly.GetExportedTypes().Sum(type => ActionKitApiHelpContent.GetDeclaredApi(type).Count()), Is.EqualTo(12));
            }
        }

        [Test]
        public void EveryIndexedTypeLinksToAUsageGuide()
        {
            var missing = ActionKitApiHelpContent.BuildIndex().Where(entry => !ActionKitApiHelpContent.RelatedTopics(entry.Type).Any())
                .Select(entry => entry.Type.FullName);
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void AllDeclaredPublicAndProtectedMembersAreShownWithoutInheritedNoise()
        {
            const BindingFlags Flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            foreach (var entry in ActionKitApiHelpContent.BuildIndex())
            {
                if (typeof(Delegate).IsAssignableFrom(entry.Type))
                {
                    continue;
                }

                var expected = entry.Type.GetMethods(Flags).Where(method => !method.IsSpecialName && Visible(method)).Cast<MemberInfo>()
                    .Concat(entry.Type.GetConstructors(Flags).Where(Visible))
                    .Concat(entry.Type.GetFields(Flags).Where(field => !field.IsSpecialName && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)))
                    .Concat(entry.Type.GetProperties(Flags).Where(property => property.GetAccessors(true).Any(Visible)))
                    .Concat(entry.Type.GetEvents(Flags).Where(signal => Visible(signal.GetAddMethod(true)))).ToArray();
                CollectionAssert.AreEquivalent(expected, entry.Members, entry.Type.FullName);
                Assert.That(entry.Members.All(member => member.DeclaringType == entry.Type), Is.True);
                Assert.That(entry.Members.Select(ActionKitApiHelpContent.FormatSignature).Distinct().Count(), Is.EqualTo(entry.Members.Length), entry.Type.FullName);
            }
        }

        private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

        [Test]
        public void ActionKitFactoryAndSevenMessageSignaturesAreAllPresent()
        {
            var entry = ActionKitApiHelpContent.BuildIndex().Single(type => type.Type == typeof(ActionKit));
            foreach (var name in new[] { "Delay", "Sequence", "DelayFrame", "NextFrame", "Lerp", "Callback", "Condition", "Repeat", "Parallel", "Custom", "Coroutine", "Task" })
            {
                Assert.That(entry.Members.Any(member => member.Name == name), Is.True, name);
            }

            Assert.That(entry.Members.OfType<MethodInfo>().Count(method => method.Name == "Custom"), Is.EqualTo(2));
            foreach (var name in new[] { "OnUpdate", "OnFixedUpdate", "OnLateUpdate", "OnGUI", "OnApplicationQuit", "OnApplicationPause", "OnApplicationFocus" })
            {
                Assert.That(entry.Members.OfType<PropertyInfo>().Any(property => property.Name == name), Is.True, name);
            }
        }

        [Test]
        public void StartOverloadsRetainCallbackKindsAndNullDefaults()
        {
            var entry = ActionKitApiHelpContent.BuildIndex().Single(type => type.Type == typeof(IActionExtensions));
            Assert.That(entry.Members.Count(member => member.Name == "Start"), Is.EqualTo(2));
            Assert.That(entry.Members.Count(member => member.Name == "StartGlobal"), Is.EqualTo(2));
            Assert.That(entry.Members.Count(member => member.Name == "StartCurrentScene"), Is.EqualTo(2));
            StringAssert.Contains("this IAction self, UnityEngine.MonoBehaviour monoBehaviour, Action<IActionController> onFinish = null", entry.Signatures);
            StringAssert.Contains("Action onFinish", entry.Signatures);
        }

        [Test]
        public void NamedStartsQueriesAndSafeHandleExposeTheirActualLeaseContracts()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            var handle = index.Single(entry => entry.Type == typeof(ActionHandle));
            Assert.That(typeof(ActionHandle).IsValueType, Is.True);
            Assert.That(typeof(ActionHandle).IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), false), Is.True);
            Assert.That(handle.Members.OfType<ConstructorInfo>(), Is.Empty);
            Assert.That(handle.Members.OfType<FieldInfo>(), Is.Empty);
            var active = handle.Members.OfType<PropertyInfo>().Single();
            Assert.That(active.Name, Is.EqualTo("IsActive"));
            Assert.That(active.PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(active.CanWrite, Is.False);
            var operations = handle.Members.OfType<MethodInfo>().ToArray();
            CollectionAssert.AreEquivalent(new[] { "Pause", "Resume", "Cancel", "SetUpdateMode" }, operations.Select(method => method.Name));
            Assert.That(operations.All(method => method.ReturnType == typeof(bool) && !method.IsStatic), Is.True);
            Assert.That(operations.Single(method => method.Name == "SetUpdateMode").GetParameters().Single().ParameterType,
                Is.EqualTo(typeof(ActionUpdateModes)));

            var starts = index.Single(entry => entry.Type == typeof(IActionExtensions));
            foreach (var name in new[] { "StartNamed", "ReplaceNamed", "StartNamedCurrentScene", "ReplaceNamedCurrentScene", "StartNamedGlobal", "ReplaceNamedGlobal" })
            {
                var methods = starts.Members.OfType<MethodInfo>().Where(method => method.Name == name).ToArray();
                Assert.That(methods.Length, Is.EqualTo(2), name);
                Assert.That(methods.All(method => method.ReturnType == typeof(ActionHandle)), Is.True, name);
                Assert.That(methods.All(method => method.GetParameters()[0].ParameterType == typeof(IAction) &&
                    method.GetParameters().Single(parameter => parameter.Name == "flag").ParameterType == typeof(string)), Is.True, name);
                CollectionAssert.AreEquivalent(new[] { typeof(Action), typeof(Action<IActionController>) },
                    methods.Select(method => method.GetParameters().Last().ParameterType), name);
                var typed = methods.Single(method => method.GetParameters().Last().ParameterType == typeof(Action<IActionController>));
                Assert.That(typed.GetParameters().Last().DefaultValue, Is.Null, name);
                Assert.That(typed.GetParameters().Last().IsOptional, Is.True, name);
            }
            var entryPoints = index.Single(entry => entry.Type == typeof(ActionKit));
            foreach (var suffix in new[] { "", "CurrentScene", "Global" })
            {
                foreach (var operation in new[] { "TryGetNamed", "ContainsNamed", "CancelNamed", "PauseNamed", "ResumeNamed" })
                {
                    var method = entryPoints.Members.OfType<MethodInfo>().Single(item => item.Name == operation + suffix);
                    Assert.That(method.IsStatic && method.ReturnType == typeof(bool), Is.True, method.Name);
                    var parameters = method.GetParameters();
                    Assert.That(parameters.Any(parameter => parameter.Name == "owner"), Is.EqualTo(suffix.Length == 0));
                    if (operation == "TryGetNamed")
                    {
                        Assert.That(parameters.Last().IsOut, Is.True);
                        Assert.That(parameters.Last().ParameterType, Is.EqualTo(typeof(ActionHandle).MakeByRefType()));
                    }
                }
            }
            Assert.That(typeof(IAction<>).GetProperties().Any(property => property.Name == "Flag"), Is.False);
            foreach (var text in new[] { "ordinal", "不消费新动作", "失败不恢复旧任务", "不初始化或创建", "ActionId=0", "Reset 不换租约", "Repeat" })
            {
                StringAssert.Contains(text, Topic("control").SearchText);
            }

            Assert.That(ActionKitApiHelpContent.RelatedTopics(typeof(ActionHandle)).Single().Id, Is.EqualTo("control"));
            StringAssert.Contains("安全", handle.Summary);
            StringAssert.Contains("失效句柄不取消后来同名任务", handle.Entries.Single(entry => entry.Name == "Cancel").Help.Returns);
            StringAssert.DoesNotContain("池化控制器引用", starts.Entries.First(entry => entry.Name == "StartNamed").Help.Returns);
        }

        [Test]
        public void FactoriesShowOptionalAndGenericParameterInformation()
        {
            StringAssert.Contains("int repeatCount = -1", Signature(typeof(ActionKit), "Repeat"));
            StringAssert.Contains("Action onLerpFinish = null", Signature(typeof(ActionKit), "Lerp"));
            var custom = typeof(ActionKit).GetMethods().Single(method => method.Name == "Custom" && method.IsGenericMethod);
            StringAssert.Contains("Custom<TData>(Action<ICustomApi<TData>> customSetting)", ActionKitApiHelpContent.FormatSignature(custom));
        }

        [Test]
        public void GenericConstraintsAndParamsAreVisible()
        {
            var allocate = typeof(AbstractAction<>).GetMethod("Allocate");
            Assert.That(allocate, Is.Not.Null);
            var execute = typeof(MonoUpdateActionExecutorExtension).GetMethod("ExecuteByUpdate");
            StringAssert.Contains("where T : UnityEngine.MonoBehaviour", ActionKitApiHelpContent.FormatSignature(execute));
            StringAssert.Contains("params string[] shortNamespaces", Signature(typeof(HelpApiFormatter), "FormatType"));
            StringAssert.Contains("where T : struct", ActionKitApiHelpContent.FormatDeclaration(typeof(Nullable<>)));
            StringAssert.Contains("new()", ActionKitApiHelpContent.FormatDeclaration(typeof(AbstractAction<>)));
        }

        [Test]
        public void PropertiesRetainPrivateProtectedAndPublicSetterVisibility()
        {
            StringAssert.Contains("{ get; private set; }", ActionKitApiHelpContent.FormatSignature(typeof(ActionKit).GetProperty("IsInitialized")));
            StringAssert.Contains("{ get; protected set; }", ActionKitApiHelpContent.FormatSignature(
                typeof(ProtectedSetterExample).GetProperty("Value")));
            StringAssert.Contains("{ get; set; }", ActionKitApiHelpContent.FormatSignature(typeof(AbstractAction<>).GetProperty("Status")));
        }

        [Test]
        public void PublicDeclarationsDoNotExposeInternalCleanupInterfaces()
        {
            foreach (var entry in ActionKitApiHelpContent.BuildIndex())
            {
                StringAssert.DoesNotContain("IActionExecutionVersion", entry.Declaration + entry.Signatures);
            }
        }

        [Test]
        public void ActionHooksShowVirtualAndOverrideModifiers()
        {
            foreach (var name in new[] { "OnStart", "OnExecute" })
            {
                StringAssert.Contains("public virtual", ActionKitApiHelpContent.FormatSignature(typeof(AbstractAction<>).GetMethod(name)));
                StringAssert.Contains("public override", ActionKitApiHelpContent.FormatSignature(
                    typeof(ScreenTransitionFade).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)));
            }
        }

        [Test]
        public void EaseListsAllCurvesDefaultsAndSentinel()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            var ease = index.Single(entry => entry.Type == typeof(EaseUtility));
            Assert.That(ease.Members.OfType<MethodInfo>().Count(method => method.IsStatic), Is.EqualTo(31));
            StringAssert.Contains("float bounciness = 1.5f", ease.Signatures);
            StringAssert.Contains("float elasticity = 0.3f, float duration = 5f", ease.Signatures);
            StringAssert.Contains("Count = 31", index.Single(entry => entry.Type == typeof(EaseType)).Signatures);
            Assert.That(ActionKitApiHelpContent.FormatDeclaration(typeof(EaseType)), Is.EqualTo("public enum EaseType"));
        }

        [Test]
        public void NestedAndGenericTypesRetainTheirDeclaringTypeAndArguments()
        {
            Assert.That(ActionKitApiHelpContent.FormatType(typeof(Dictionary<string, IAction>.Enumerator)),
                Is.EqualTo("Dictionary<string, IAction>.Enumerator"));
            Assert.That(ActionKitApiHelpContent.FormatType(typeof(Dictionary<string, IAction>.KeyCollection.Enumerator)),
                Is.EqualTo("Dictionary<string, IAction>.KeyCollection.Enumerator"));
            Assert.That(ActionKitApiHelpContent.FormatType(typeof(ScreenTransitionInOut<IAction, IAction>)),
                Is.EqualTo("ScreenTransitionInOut<IAction, IAction>"));
            Assert.That(ActionKitApiHelpContent.FormatType(typeof(ActionKit.ScreenTransition)), Is.EqualTo("ActionKit.ScreenTransition"));
        }

        [Test]
        public void DelegateDeclarationsShowTheirCallSignature()
        {
            var declaration = ActionKitApiHelpContent.FormatDeclaration(typeof(Action<IActionController>));
            StringAssert.Contains("public delegate void Action<IActionController>(IActionController obj);", declaration);
        }

        [Test]
        public void ProtectedHooksAreDiscoverableButPrivatePoolImplementationIsNot()
        {
            var entry = ActionKitApiHelpContent.BuildIndex().Single(type => type.Type == typeof(AbstractAction<>));
            StringAssert.Contains("protected virtual void OnReset()", entry.Signatures);
            StringAssert.Contains("protected virtual void OnDeinit()", entry.Signatures);
            StringAssert.Contains("protected static ActionKit Architecture", ActionKitApiHelpContent.FormatSignature(
                typeof(ActionKit).GetField("Architecture", BindingFlags.NonPublic | BindingFlags.Static)));
            StringAssert.DoesNotContain("_pool", entry.Signatures);
            Assert.That(ActionKitApiHelpContent.BuildIndex().Any(type => type.Type.Name == "ScreenTransitionCanvas" || type.Type.Name.StartsWith("ActionPool")), Is.False);
        }

        [Test]
        public void TopicIdsAreUniqueAndUserFacingDescriptionsAreChinese()
        {
            var topics = ActionKitApiHelpContent.Topics;
            Assert.That(topics.Select(topic => topic.Id).Distinct().Count(), Is.EqualTo(topics.Length));
            Assert.That(topics.All(topic => HasChinese(topic.Title) && HasChinese(topic.Summary) && HasChinese(topic.Behavior)), Is.True);
            Assert.That(topics.All(topic => !topic.Title.Contains(" / ")), Is.True);
            Assert.That(topics.All(topic => !string.IsNullOrWhiteSpace(topic.Notes)), Is.True);
            Assert.That(topics.All(topic => HasChinese(topic.Notes) && HasChinese(topic.ExampleContext)), Is.True);
        }

        private static bool HasChinese(string text) => text.Any(character => character >= '\u4e00' && character <= '\u9fff');

        [TestCase(" DELAY ", "delay")]
        [TestCase("\u534f\u7a0b", "coroutine")]
        [TestCase("Composition", "sequence")]
        [TestCase("Interface", "lifecycle")]
        [TestCase("DOTween", "distribution")]
        [TestCase("IObservable", "observable")]
        [TestCase("DOTweenAction", "dotween")]
        public void GuideSearchFindsNamesDescriptionsAndExamples(string search, string topic)
        {
            Assert.That(ActionKitApiHelpContent.Topics.Single(entry => entry.Id == topic).Matches(search), Is.True);
        }

        [Test]
        public void IndexSearchFindsMembersNotJustTypeNames()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            Assert.That(index.Single(entry => entry.Type == typeof(ActionKit)).Matches("OnApplicationFocus"), Is.True);
            Assert.That(index.Single(entry => entry.Type == typeof(IActionExtensions)).Matches("StartCurrentScene"), Is.True);
            Assert.That(index.Single(entry => entry.Type == typeof(EaseUtility)).Matches("elasticity"), Is.True);
            Assert.That(index.All(entry => !entry.Matches("DefinitelyNotAnActionKitSymbol")), Is.True);
        }

        [Test]
        public void IndexIsStableAcrossRebuildsAndBlankSearchMatchesAllEntries()
        {
            var first = ActionKitApiHelpContent.BuildIndex();
            var second = ActionKitApiHelpContent.BuildIndex();
            CollectionAssert.AreEqual(first.Select(entry => entry.Declaration + entry.Signatures), second.Select(entry => entry.Declaration + entry.Signatures));
            Assert.That(first.All(entry => entry.Matches(" ")), Is.True);
            Assert.That(ActionKitApiHelpContent.Topics.All(entry => entry.Matches(null)), Is.True);
        }

        [Test]
        public void BrowsingContentDoesNotInitializeRuntimeOrCreateGameObjects()
        {
            var initialized = ActionKit.IsInitialized;
            var nextId = ActionKit.IdGenerator;
            var sceneHost = ActionKitCurrentScene.CachedSceneComponent;
            var objects = Resources.FindObjectsOfTypeAll<GameObject>().Select(item => item.GetInstanceID()).OrderBy(id => id).ToArray();
            ActionKitApiHelpContent.BuildIndex();
            ActionKitApiHelpContent.ExampleCompilationSource();
            Assert.That(ActionKit.IsInitialized, Is.EqualTo(initialized));
            Assert.That(ActionKit.IdGenerator, Is.EqualTo(nextId));
            Assert.That(ActionKitCurrentScene.CachedSceneComponent, Is.SameAs(sceneHost));
            CollectionAssert.AreEqual(objects, Resources.FindObjectsOfTypeAll<GameObject>().Select(item => item.GetInstanceID()).OrderBy(id => id));
        }

        [Test]
        public void KnownIntegrationAndOwnershipBoundariesAreExplicit()
        {
            StringAssert.Contains("Interface 只返回当前 ActionKit 模块实例", Topic("lifecycle").SearchText);
            StringAssert.Contains("不提供系统、模型、工具的注册", Topic("lifecycle").SearchText);
            StringAssert.Contains("不会 Dispose 或 Cancel", Topic("task").SearchText);
            StringAssert.Contains("父动作取消也不会取消", Topic("screen").SearchText);
            StringAssert.Contains("父动作不会随它完成", Topic("screen").SearchText);
            StringAssert.Contains("子动作取消会取消父序列", Topic("sequence").SearchText);
            StringAssert.Contains("当前可选 DOTween 动作集成已可用", Topic("distribution").SearchText);
            StringAssert.Contains("完整 Integrations/DOTween 文件夹", Topic("distribution").SearchText);
            StringAssert.DoesNotContain("未安装也未实现", Topic("distribution").SearchText);
            StringAssert.Contains("内建 Observable BCL 适配", Topic("distribution").SearchText);
            StringAssert.Contains("不再提供 Action 单文件替代安装", Topic("distribution").SearchText);
            StringAssert.Contains("YLFramework.Runtime", Topic("distribution").SearchText);
            StringAssert.Contains("不要同时安装两份 Runtime", Topic("distribution").SearchText);
            StringAssert.DoesNotContain("当前没有提供 DOTween 或 Observable", Topic("distribution").SearchText);
            StringAssert.Contains("Time.frameCount", Topic("frames").SearchText);
            StringAssert.Contains("不是按动作实际获得的更新次数", Topic("frames").SearchText);
            var module = ActionKitHelpProvider.CreateModule();
            StringAssert.Contains("YLFramework", module.Documentation(typeof(ActionKit)).Summary);
            foreach (var page in module.Guides.Concat(module.About))
            {
                Assert.That(page.RelatedTypes.All(type => type.Assembly == typeof(ActionKit).Assembly ||
                    type.Assembly.GetName().Name == "YLFramework.Actions.DOTween"), Is.True, page.Id);
            }
        }

        [Test]
        public void ActionKitProviderRegistersItsMenuAsAShortcutToTheCommonEditorWindow()
        {
            ActionKitHelpProvider.CreateModule();
            Assert.That(typeof(ActionKitHelpProvider).Assembly.GetName().Name, Is.EqualTo("YLFramework.Editor"));
            Assert.That(ActionKitHelpProvider.ModuleId, Is.EqualTo("actionkit"));
            Assert.That(typeof(ActionKitHelpProvider).Namespace, Is.EqualTo("YLFramework.Editor.Actions"));
            Assert.That(typeof(ActionKitHelpProvider).IsDefined(typeof(InitializeOnLoadAttribute), false), Is.True);
            Assert.That(typeof(HelpWindow).Assembly, Is.SameAs(typeof(ActionKitHelpProvider).Assembly));
            Assert.That(typeof(EditorWindow).IsAssignableFrom(typeof(HelpWindow)), Is.True);
            Assert.That(typeof(HelpWindow).GetMethod("Open", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            var registered = HelpRegistry.GetModules().Single(module => module.Id == ActionKitHelpProvider.ModuleId);
            Assert.That(registered.DisplayName, Is.EqualTo("ActionKit"));
            CollectionAssert.AreEquivalent(ActionKitApiHelpContent.Topics.Select(topic => topic.Id), registered.Create().Guides.Select(page => page.Id));
            var menu = typeof(ActionKitHelpProvider).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static)
                .GetCustomAttribute<MenuItem>();
            Assert.That(menu.menuItem, Is.EqualTo("YLFramework/Action/帮助"));
            var asmdef = File.ReadAllText(Path.Combine(Application.dataPath, "YLFramework/Editor/YLFramework.Editor.asmdef"));
            StringAssert.IsMatch("\"includePlatforms\"\\s*:\\s*\\[\\s*\"Editor\"\\s*\\]", asmdef);
            StringAssert.DoesNotContain("\"YLFramework.Editor.Help\"", asmdef);
        }

        [Test]
        public void ProviderPagesRetainGuideBodiesExamplesAndExplicitTypeAssociations()
        {
            var module = ActionKitHelpProvider.CreateModule();
            var index = HelpApiIndex.Build(module);
            CollectionAssert.AreEqual(ActionKitApiHelpContent.GuideGroups, module.Guides.Select(page => page.Group).Distinct());
            CollectionAssert.AreEquivalent(ActionKitApiHelpContent.Topics.Select(topic => topic.Id), module.Guides.Select(page => page.Id));
            foreach (var topic in ActionKitApiHelpContent.Topics)
            {
                var page = module.Guides.Single(entry => entry.Id == topic.Id);
                Assert.That(page.Title, Is.EqualTo(topic.Title));
                Assert.That(page.Group, Is.EqualTo(topic.Group));
                Assert.That(page.Summary, Is.EqualTo(topic.Summary));
                CollectionAssert.AreEqual(topic.TypeNames, page.Keywords);
                if (topic.Example.Length != 0)
                {
                    var code = page.Blocks.Single(block => block.Kind == HelpBlockKind.Code);
                    Assert.That(code.Content, Is.EqualTo(topic.Example));
                    Assert.That(code.Context, Is.EqualTo(topic.ExampleContext));
                }
                else
                {
                    Assert.That(page.Blocks.Any(block => block.Kind == HelpBlockKind.Code), Is.False);
                    Assert.That(page.Blocks.Single(block => block.Title == "使用方式").Content, Is.EqualTo(topic.ExampleContext));
                }
                Assert.That(page.Blocks.Single(block => block.Title == "参数、执行与生命周期").Content, Is.EqualTo(topic.Behavior));
                Assert.That(page.Blocks.Single(block => block.Title == "注意事项").Content, Is.EqualTo(topic.Notes));
                Assert.That(page.Blocks.Any(block => block.Title == "可选程序集未加载"), Is.EqualTo(!topic.IsAvailable));
                CollectionAssert.AreEquivalent(index.Where(type => ActionKitApiHelpContent.RelatedTopics(type.Type).Contains(topic))
                    .Select(type => type.Type), page.RelatedTypes);
                CollectionAssert.AreEquivalent(ActionKitApiHelpContent.FeaturedTypes(topic, index).Select(type => type.Type), page.FeaturedTypes);
            }
            foreach (var type in index)
            {
                CollectionAssert.AreEquivalent(ActionKitApiHelpContent.RelatedTopics(type.Type).Select(topic => topic.Id), type.RelatedPages.Select(page => page.Id));
            }

            Assert.That(module.Guides.Single(page => page.Id == "screen").FeaturedTypes, Does.Contain(typeof(ActionKit.ScreenTransition)));
            Assert.That(module.CollapsedGuideGroups, Is.Empty);
            Assert.That(module.CollapsedApiGroups, Is.Empty);
        }

        [Test]
        public void ProviderAboutRetainsVersionAndUsageBoundary()
        {
            var about = ActionKitHelpProvider.CreateModule().About;
            CollectionAssert.AreEqual(new[] { "usage" }, about.Select(page => page.Id));
            var usage = about.Single(page => page.Id == "usage").SearchText;
            StringAssert.Contains(Application.unityVersion, usage);
            StringAssert.Contains(typeof(ActionKit).Assembly.GetName().Version.ToString(), usage);
            StringAssert.Contains("不执行示例、不创建动作宿主", usage);
        }

        private static ActionKitApiHelpContent.Topic Topic(string id) => ActionKitApiHelpContent.Topics.Single(topic => topic.Id == id);
        private static string Signature(Type type, string name) => ActionKitApiHelpContent.FormatSignature(type.GetMethod(name));

        [Test]
        public void ExecuteReferenceDistinguishesTerminalResultFromSuccessfulCompletion()
        {
            var help = ActionKitApiHelpContent.BuildIndex().Single(type => type.Type == typeof(IActionExtensions))
                .Entries.Single(entry => entry.Name == "Execute").Help;
            StringAssert.Contains("取消后再标记 Finished", help.Summary);
            StringAssert.Contains("不一定表示成功", help.Returns);
            StringAssert.Contains("不调用结束钩子", help.Remarks);
            StringAssert.Contains("IsDeinitialized", help.Returns);
        }

        [Test]
        public void AllModernGuidesRemainInTheFourNavigationGroups()
        {
            var currentGuideIds = new[] { "overview", "lifecycle", "delay", "frames", "condition", "sequence", "parallel", "repeat", "custom", "coroutine",
                "task", "control", "messages", "subscriptions", "lerp", "screen", "extend", "distribution", "observable", "dotween" };
            Assert.That(currentGuideIds.Length, Is.EqualTo(20));
            Assert.That(ActionKitApiHelpContent.GuideGroups.Length, Is.EqualTo(4));
            CollectionAssert.AreEquivalent(currentGuideIds, ActionKitApiHelpContent.Topics.Select(topic => topic.Id));
            Assert.That(ActionKitApiHelpContent.Topics.All(topic => ActionKitApiHelpContent.GuideGroups.Contains(topic.Group)), Is.True);
            Assert.That(ActionKitApiHelpContent.GuideGroups.All(group => ActionKitApiHelpContent.Topics.Any(topic => topic.Group == group)), Is.True);
        }

        [Test]
        public void GettingStartedIsACompleteMonoBehaviourScriptWithARealEntryPoint()
        {
            var source = ActionKitApiHelpContent.GettingStartedSource;
            StringAssert.Contains("using UnityEngine;", source);
            StringAssert.Contains("using YLFramework.Actions;", source);
            StringAssert.DoesNotContain("using Kit =", source);
            StringAssert.Contains("ActionKit.Sequence()", source);
            StringAssert.Contains("public class ActionKitQuickStart : MonoBehaviour", source);
            StringAssert.Contains("private void Start()", source);
            StringAssert.Contains(".Start(this)", source);
            StringAssert.DoesNotContain("ActionKit.Initialize", source);
            StringAssert.DoesNotContain("UniRx", source);
            StringAssert.DoesNotContain("IObservable", source);
            StringAssert.DoesNotContain("DOTween", source);
            Assert.That(Topic("overview").RequiredAssembly, Is.Null);
            StringAssert.Contains("ActionKitQuickStart.cs", Topic("overview").ExampleContext);
            StringAssert.DoesNotContain("public class ActionKitQuickStart", ActionKitApiHelpContent.ExampleCompilationSource());
        }

        [Test]
        public void GuidesUseTheSpecificActionKitNameWithoutAGenericKitAlias()
        {
            foreach (var topic in ActionKitApiHelpContent.Topics)
            {
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(topic.SearchText, @"\bKit\."), Is.False, topic.Id);
                StringAssert.DoesNotContain("using Kit =", topic.SearchText);
            }
            var source = ActionKitApiHelpContent.ExampleCompilationSource();
            StringAssert.DoesNotContain("using Kit =", source);
            StringAssert.Contains("namespace ActionKitHelpExamples", source);
            StringAssert.DoesNotContain("namespace YLFramework.Actions.", source);
        }

        [Test]
        public void GettingStartedLinksOnlyTheEntrySequenceAndStartContract()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            var types = ActionKitApiHelpContent.FeaturedTypes(Topic("overview"), index).Select(entry => entry.Type).ToArray();
            CollectionAssert.AreEquivalent(new[] { typeof(ActionKit), typeof(ISequence), typeof(IActionExtensions) }, types);
            Assert.That(types.Any(type => type.Name.EndsWith("Attribute", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void FeaturedLinksResolveWithoutLosingNestedScreenTransition()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            foreach (var topic in ActionKitApiHelpContent.Topics)
            {
                var links = ActionKitApiHelpContent.FeaturedTypes(topic, index).ToArray();
                Assert.That(links.Select(entry => entry.Type).Distinct().Count(), Is.EqualTo(links.Length));
                foreach (var name in topic.FeaturedTypeNames)
                {
                    if (topic.RequiredAssembly == "YLFramework.Actions.DOTween" && !topic.IsAvailable &&
                        (name == "DOTweenAction" || name == "ActionKitDOTweenExtension"))
                    {
                        continue;
                    }

                    Assert.That(links.Any(entry => entry.Name == name || entry.Type.Name.Split('`')[0] == name), Is.True, topic.Id + ": " + name);
                }
            }
            Assert.That(ActionKitApiHelpContent.FeaturedTypes(Topic("screen"), index).Any(entry => entry.Type == typeof(ActionKit.ScreenTransition)), Is.True);
        }

        [Test]
        public void OptionalGuideAvailabilityControlsOnlyItsShortCompilationFragment()
        {
            var observable = Topic("observable");
            Assert.That(observable.RequiredAssembly, Is.Null);
            Assert.That(observable.IsAvailable, Is.True);
            Assert.That(typeof(UniRxAction<>).Assembly, Is.SameAs(typeof(ActionKit).Assembly));
            Assert.That(typeof(ActionKitUniRxExtension).Assembly, Is.SameAs(typeof(ActionKit).Assembly));
            Assert.That(ActionKitApiHelpContent.Topics.Where(topic => topic.Id != "observable" && topic.Id != "dotween")
                .All(topic => topic.RequiredAssembly == null && topic.IsAvailable), Is.True);
            var dotween = Topic("dotween");
            Assert.That(dotween.RequiredAssembly, Is.EqualTo("YLFramework.Actions.DOTween"));
            Assert.That(dotween.IsAvailable, Is.EqualTo(AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == dotween.RequiredAssembly)));
            CollectionAssert.AreEquivalent(new[] { "DOTweenAction", "ActionKitDOTweenExtension", "IAction", "ISequence" }, dotween.TypeNames);
            CollectionAssert.AreEquivalent(dotween.TypeNames, dotween.FeaturedTypeNames);
            CollectionAssert.AreEquivalent(new[] { "UniRxAction", "ActionKitUniRxExtension", "IAction", "ISequence" }, observable.TypeNames);
            CollectionAssert.AreEquivalent(observable.TypeNames, observable.FeaturedTypeNames);
            var source = ActionKitApiHelpContent.ExampleCompilationSource();
            foreach (var topic in ActionKitApiHelpContent.Topics.Where(topic => !topic.IsCompleteExample && topic.Example.Length != 0))
            {
                Assert.That(source.Contains(topic.Example), Is.EqualTo(topic.IsAvailable), topic.Id);
            }

            StringAssert.Contains("IObservable<int>", observable.Example);
            StringAssert.Contains("IDisposable", observable.Example);
            StringAssert.Contains("mSource.mObserver = null", observable.Example);
            StringAssert.DoesNotContain("MonoBehaviour\n{", observable.Example);
            StringAssert.DoesNotContain("using UniRx", source);
            StringAssert.Contains("using YLFramework.Actions;", source);
            Assert.That(source.Contains("using DG.Tweening;"), Is.EqualTo(dotween.IsAvailable));
            StringAssert.Contains("DG.Tweening.DOTween.To", dotween.Example);
            StringAssert.Contains("float value = 0f;", dotween.Example);
            StringAssert.DoesNotContain("DOMove", dotween.Example);
            StringAssert.Contains("using DG.Tweening;", dotween.ExampleContext);
        }

        [Test]
        public void DOTweenChineseHelpDocumentsOwnedTweenLifecycleAndOptionalDependency()
        {
            var topic = Topic("dotween");
            Assert.That(topic.Group, Is.EqualTo("扩展与工具"));
            foreach (var text in new[] { "getter 恰一次", "Complete()", "Kill(true)", "外部 Pause 只是等待", "Kill(false)",
                "Resume 只恢复由动作暂停", "包含 paused Tween", "OnFinish 仅 detach hooks", "autoKill=false", "source owner",
                "SetUpdate(Manual/independent)", "IgnoreTimeScale 不自动改变 Tween clock", "onComplete/onKill", "onKill marker",
                "ownership 无法证实", "onPause 不清理", "safeMode", "multicast", "不能承诺逐 callback 异常隔离",
                "独立根 Tween", "DG.Tweening.Sequence 本身", "内部子 Tween", "isSequenced", "YLFramework.Actions.Sequence",
                "1.2.825",
                "Pause 前预绑定", "ToAction 是单次绑定", "首次启动前 Reset 保留同一预绑定", "Reset 结束原绑定，再 Start 会取消", "不重新认领",
                "原始 UnitySynchronizationContext", "完整类型名", "程序集身份", "sealed", "主线程 ID",
                "不调用 factory、不 Pause Tween", "不支持自定义同步 Post 或人工 pump", "UnitySynchronizationContext.Post 延后",
                "Deinit 立即标记 IsDeinitialized", "Reset 立即恢复 NotStart", "pending marker", "实际主线程", "其他 owner",
                "Shutdown 不保证同步物理 Kill", "Domain Reload", "Post 未执行", "可能没有物理清理",
                "不保证物理清理", "不能完全区分", "漏掉完成或取消信号" })
            {
                StringAssert.Contains(text, topic.SearchText);
            }

            foreach (var type in ActionKitApiHelpContent.BuildIndex().Where(entry => entry.Type.Assembly.GetName().Name == topic.RequiredAssembly))
            {
                StringAssert.DoesNotContain("尚未核实", type.Summary);
                Assert.That(type.Members.Any(member => member is ConstructorInfo), Is.False);
                foreach (var entry in type.Entries)
                {
                    Assert.That(HasChinese(entry.Help.Summary) && HasChinese(entry.Help.Remarks), Is.True, entry.Signature);
                    if (entry.Member is MethodInfo method && method.ReturnType != typeof(void))
                    {
                        Assert.That(HasChinese(entry.Help.Returns), Is.True, entry.Signature);
                    }

                    StringAssert.DoesNotContain("尚未核实", entry.SearchText);
                    foreach (var text in new[] { "原始 UnitySynchronizationContext", "不支持自定义同步 Post 或人工 pump",
                        "不调用 factory、不 Pause Tween", "pending marker", "Post 延后", "Shutdown 不保证同步物理 Kill",
                        "Domain Reload", "不保证物理清理", "不能完全区分", "漏掉完成或取消信号" })
                    {
                        StringAssert.Contains(text, entry.Help.Remarks, entry.Signature);
                    }
                }
                if (type.Type.Name == "ActionKitDOTweenExtension")
                {
                    var bound = type.Entries.Single(entry => entry.Name == "ToAction");
                    StringAssert.Contains("Pause 前预绑定", bound.Help.Summary);
                    StringAssert.Contains("Reset 后再 Start 取消", bound.Help.Returns);
                    StringAssert.Contains("Reset 后不可重新认领", bound.Help.Parameters.Single().Description);
                }
                if (type.Type.Name != "DOTweenAction")
                {
                    continue;
                }

                Assert.That(type.Members.Length, Is.EqualTo(10));
                Assert.That(type.Members.OfType<MethodInfo>().Count(), Is.EqualTo(6));
                Assert.That(type.Members.OfType<PropertyInfo>().Count(), Is.EqualTo(4));
                StringAssert.Contains("不 Kill", type.Entries.Single(entry => entry.Name == "OnFinish").Help.Summary);
                StringAssert.Contains("保留 getter", type.Entries.Single(entry => entry.Name == "Reset").Help.Summary);
                StringAssert.Contains("清空 getter", type.Entries.Single(entry => entry.Name == "Deinit").Help.Summary);
                StringAssert.Contains("只恢复由此动作暂停", type.Entries.Single(entry => entry.Name == "IsPaused").Help.Summary);
                StringAssert.Contains("执行与取消", type.Entries.Single(entry => entry.Name == "Allocate").Help.Parameters.Single().Description);
                StringAssert.Contains("首先校验", type.Entries.Single(entry => entry.Name == "OnStart").Help.Summary);
                StringAssert.Contains("只复用预绑定", type.Entries.Single(entry => entry.Name == "OnStart").Help.Summary);
                StringAssert.Contains("再 Start 会取消", type.Entries.Single(entry => entry.Name == "Reset").Help.Summary);
                StringAssert.Contains("Post 延后", type.Entries.Single(entry => entry.Name == "Reset").Help.Summary);
                StringAssert.Contains("立即标记 IsDeinitialized", type.Entries.Single(entry => entry.Name == "Deinit").Help.Summary);
                StringAssert.Contains("Post 延后", type.Entries.Single(entry => entry.Name == "Deinit").Help.Summary);
            }
        }

        [Test]
        public void ObservableChineseHelpDocumentsNotificationOwnershipAndErrorBoundaries()
        {
            var topic = Topic("observable");
            Assert.That(topic.Group, Is.EqualTo("扩展与工具"));
            foreach (var text in new[] { "不需要第三方 UniRx SDK", "首个 OnNext", "OnCompleted 取消", "OnError 记录异常并取消",
                "OnFinish 保留 factory 到 Deinit", "Reset 释放旧 subscription 并保留 factory", "不 Dispose source 本体",
                "同步 Subscribe 返回前", "只释放一次", "旧订阅通知", "Dispose 错误可见", "Deinit 仍安排回池",
                "Unity 主线程", "SynchronizationContext", "OnExecute 消费", "Pause 不暂停 source",
                "YLFramework.Runtime", "内建" })
            {
                StringAssert.Contains(text, topic.SearchText);
            }

            var observableTypes = new[] { typeof(UniRxAction<>), typeof(ActionKitUniRxExtension) };
            var observableEntries = ActionKitApiHelpContent.BuildIndex().Where(entry => observableTypes.Contains(entry.Type)).ToArray();
            CollectionAssert.AreEquivalent(observableTypes, observableEntries.Select(entry => entry.Type));
            foreach (var type in observableEntries)
            {
                StringAssert.DoesNotContain("尚未核实", type.Summary);
                Assert.That(type.Members.Any(member => member is ConstructorInfo), Is.False);
                StringAssert.DoesNotContain("Subscription", type.Declaration + type.Signatures);
                foreach (var entry in type.Entries)
                {
                    Assert.That(HasChinese(entry.Help.Summary) && HasChinese(entry.Help.Remarks), Is.True, entry.Signature);
                    if (entry.Member is MethodInfo method && method.ReturnType != typeof(void))
                    {
                        Assert.That(HasChinese(entry.Help.Returns), Is.True, entry.Signature);
                    }

                    StringAssert.DoesNotContain("尚未核实", entry.SearchText);
                }
                if (type.Type.Name.Split('`')[0] != "UniRxAction")
                {
                    continue;
                }

                StringAssert.Contains("保留 factory 到 Deinit", type.Entries.Single(entry => entry.Name == "OnFinish").Help.Summary);
                StringAssert.Contains("释放旧 subscription", type.Entries.Single(entry => entry.Name == "Reset").Help.Summary);
                StringAssert.Contains("清空 factory 引用", type.Entries.Single(entry => entry.Name == "Deinit").Help.Summary);
            }
        }

        [TestCase("  Delay   等待 ", "delay", true)]
        [TestCase("Task 取消", "task", true)]
        [TestCase("Task 不存在的关键词", "task", false)]
        public void SearchRequiresAllWordsAndSupportsChineseAndIdentifiers(string search, string id, bool matches)
        {
            Assert.That(Topic(id).Matches(search), Is.EqualTo(matches));
        }

        [Test]
        public void ApiReferencePreservesVerifiedChineseOverridesAndUsesXmlOrExplicitMissingFields()
        {
            var module = ActionKitHelpProvider.CreateModule();
            var index = HelpApiIndex.Build(module);
            module.Documentation = null;
            var xml = HelpApiIndex.Build(module);
            foreach (var type in index)
            {
                Assert.That(HasChinese(type.Summary), Is.True, type.Name);
                Assert.That(!string.IsNullOrWhiteSpace(type.Group), Is.True);
                CollectionAssert.AreEquivalent(type.Members, type.Entries.Select(entry => entry.Member));
                var xmlType = xml.Single(entry => entry.Type == type.Type);
                AssertDocumentationSources(type.Help, ActionKitApiHelpReference.Describe(type.Type), xmlType.Help, type.Name);
                foreach (var entry in type.Entries)
                {
                    var verified = ActionKitApiHelpReference.Describe(entry.Member);
                    var xmlMember = xmlType.Entries.Single(item => item.Member == entry.Member);
                    AssertDocumentationSources(entry.Help, verified, xmlMember.Help, entry.Signature);
                    Assert.That(string.IsNullOrWhiteSpace(entry.Help.Summary) || HasChinese(entry.Help.Summary), Is.True, entry.Signature);
                    var parameters = entry.Member is MethodBase method ? method.GetParameters() : entry.Member is PropertyInfo property ? property.GetIndexParameters() : Array.Empty<ParameterInfo>();
                    CollectionAssert.IsSubsetOf(entry.Help.Parameters.Select(parameter => parameter.Name), parameters.Select(parameter => parameter.Name), entry.Signature);
                    Assert.That(entry.Help.Parameters.All(parameter => string.IsNullOrWhiteSpace(parameter.Description) || HasChinese(parameter.Description)), Is.True, entry.Signature);
                    StringAssert.DoesNotContain("尚未核实", entry.SearchText);
                }
            }
        }

        private static void AssertDocumentationSources(HelpDocumentation actual, HelpDocumentation verified, HelpDocumentation xml, string context)
        {
            Assert.That(actual.Summary, Is.EqualTo(string.IsNullOrWhiteSpace(verified.Summary) ? xml.Summary : verified.Summary), context);
            Assert.That(actual.Returns, Is.EqualTo(string.IsNullOrWhiteSpace(verified.Returns) ? xml.Returns : verified.Returns), context);
            Assert.That(actual.Remarks, Is.EqualTo(string.IsNullOrWhiteSpace(verified.Remarks) ? xml.Remarks : verified.Remarks), context);
            foreach (var fields in new[]
            {
                new[] { actual.Parameters, verified.Parameters, xml.Parameters },
                new[] { actual.TypeParameters, verified.TypeParameters, xml.TypeParameters }
            })
            {
                var expected = fields[2].Concat(fields[1]).Where(parameter => !string.IsNullOrWhiteSpace(parameter.Description))
                    .GroupBy(parameter => parameter.Name).Select(group => group.Last()).ToArray();
                CollectionAssert.AreEquivalent(expected.Select(parameter => parameter.Name + "\n" + parameter.Description),
                    fields[0].Where(parameter => !string.IsNullOrWhiteSpace(parameter.Description))
                        .Select(parameter => parameter.Name + "\n" + parameter.Description), context);
            }
        }

        [Test]
        public void ReferenceLeavesUnknownAndDeclarationOnlyFieldsForXmlInsteadOfInventingAnOverride()
        {
            Assert.That(ActionKitApiHelpReference.TypeSummary(typeof(ApiHelpTests)), Is.Null);
            var unknown = ActionKitApiHelpReference.Describe(typeof(ApiHelpTests).GetMethod(nameof(IndexContainsEveryExportedApiTypeExactlyOnceAndRetainsAllCoreTypes)));
            Assert.That(unknown.Summary + unknown.Returns + unknown.Remarks, Is.Empty);
            Assert.That(unknown.Parameters, Is.Empty);
            var constructor = typeof(CallbackAction).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            Assert.That(constructor, Is.Not.Null);
            Assert.That(ActionKitApiHelpReference.Describe(constructor).Summary, Is.Empty);
            Assert.That(ActionKitApiHelpReference.Describe(typeof(IAction<>).GetProperty("ActionId")).Returns, Is.Empty);
            Assert.That(ActionKitApiHelpReference.Describe(typeof(IAction<>).GetMethod("OnStart")).Returns, Is.Empty);
        }

        [Test]
        public void LocalCompilerXmlFeedsTypeParametersWithoutReplacingVerifiedFrameSemantics()
        {
            var module = ActionKitHelpProvider.CreateModule();
            Assert.That(module.XmlPaths.Count, Is.EqualTo(ActionKitApiHelpContent.ApiAssemblies().Count()));
            foreach (var assembly in ActionKitApiHelpContent.ApiAssemblies())
            {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", assembly.GetName().Name + ".xml"));
                Assert.That(module.XmlPaths[assembly], Is.EqualTo(path));
                Assert.That(File.Exists(path), Is.True, "Unity must generate the local -doc output: " + path);
                var folder = assembly == typeof(ActionKit).Assembly ? "Runtime" : "Integrations/DOTween/Runtime";
                Assert.That(File.ReadAllText(Path.Combine(Application.dataPath, "YLFramework", folder, "csc.rsp")).Trim(),
                    Is.EqualTo("-doc:Library/" + assembly.GetName().Name + ".xml"));
                var document = new System.Xml.XmlDocument();
                document.Load(path);
                Assert.That(document.SelectSingleNode("/doc/assembly/name").InnerText, Is.EqualTo(assembly.GetName().Name));
                var names = document.SelectNodes("/doc/members/member").Cast<System.Xml.XmlNode>()
                    .Select(node => node.Attributes["name"].Value).ToArray();
                CollectionAssert.IsSubsetOf(assembly == typeof(ActionKit).Assembly ? new[]
                {
                    "T:YLFramework.Actions.ActionKit", "T:YLFramework.Actions.UniRxAction`1",
                    "T:YLFramework.Actions.ActionKitUniRxExtension", "M:YLFramework.Actions.ActionKit.Shutdown"
                } : new[] { "T:YLFramework.Integrations.DOTween.DOTweenAction", "T:YLFramework.Integrations.DOTween.ActionKitDOTweenExtension" }, names);
            }
            var index = HelpApiIndex.Build(module);
            var action = index.Single(type => type.Type == typeof(ActionKit));
            var custom = action.Entries.Single(entry => entry.Name == "Custom" && ((MethodInfo)entry.Member).IsGenericMethod);
            Assert.That(custom.Help.TypeParameters.Single().Name, Is.EqualTo("TData"));
            StringAssert.Contains("数据类型", custom.Help.TypeParameters.Single().Description);
            var contract = index.Single(type => type.Type == typeof(IAction<>));
            Assert.That(contract.Help.TypeParameters.Single().Name, Is.EqualTo("TStatus"));
            StringAssert.Contains("状态类型", contract.Help.TypeParameters.Single().Description);
            var frames = action.Entries.Single(entry => entry.Name == "DelayFrame");
            StringAssert.Contains("Unity 全局帧计数", frames.Help.Summary);
            StringAssert.Contains("不是执行钩子调用次数", frames.Help.Parameters.Single(parameter => parameter.Name == "frameCount").Description);
            StringAssert.Contains("Unity 全局帧计数增加一帧", action.Entries.Single(entry => entry.Name == "NextFrame").Help.Summary);
            module.Documentation = null;
            var xmlAction = HelpApiIndex.Build(module).Single(type => type.Type == typeof(ActionKit));
            StringAssert.Contains("执行器更新次数", xmlAction.Entries.Single(entry => entry.Name == "DelayFrame").Help.Summary);
            Assert.That(frames.Help.Summary, Is.EqualTo(ActionKitApiHelpReference.Describe(frames.Member).Summary));
            Assert.That(custom.Help.TypeParameters.Single().Description,
                Is.EqualTo(xmlAction.Entries.Single(entry => entry.Name == "Custom" && ((MethodInfo)entry.Member).IsGenericMethod).Help.TypeParameters.Single().Description));
        }

        [Test]
        public void ChineseMemberDescriptionsParticipateInApiSearch()
        {
            var index = ActionKitApiHelpContent.BuildIndex();
            var entry = index.Single(type => type.Type == typeof(ActionKit));
            Assert.That(entry.Matches("Delay 等待"), Is.True);
            Assert.That(entry.Entries.Single(member => member.Name == "Shutdown").SearchText, Does.Contain("关闭"));
            Assert.That(entry.Entries.Any(member => ActionKitApiHelpContent.Contains(member.SearchText, "Delay 等待")), Is.True);
        }

        [Test]
        public void UserFacingHelpUsesTypedChineseHandlesInsteadOfGenericTokenTerminology()
        {
            foreach (var topic in ActionKitApiHelpContent.Topics)
            {
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(topic.SearchText, @"\btoken\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase), Is.False, topic.Id);
            }

            foreach (var type in ActionKitApiHelpContent.BuildIndex())
            {
                var descriptions = type.Summary + "\n" + string.Join("\n", type.Entries.Select(entry =>
                    entry.Help.Summary + "\n" + entry.Help.Returns + "\n" + entry.Help.Remarks + "\n" +
                    string.Join("\n", entry.Help.Parameters.Select(parameter => parameter.Description))));
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(descriptions, @"\btoken\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase), Is.False, type.Name);
            }
            StringAssert.Contains("注销句柄（IUnregister）", Topic("subscriptions").Summary);
            StringAssert.Contains("IUnregister unregister", Topic("subscriptions").Example);
            StringAssert.Contains("幂等", Topic("subscriptions").SearchText);
            StringAssert.Contains("YLFramework.Events", Topic("subscriptions").SearchText);
        }

        [Test]
        public void MemberHighlightsIncludeTypeContextWithoutMarkingPureTypeQueries()
        {
            var type = ActionKitApiHelpContent.BuildIndex().Single(entry => entry.Type == typeof(ActionKit));
            var delay = type.Entries.Single(entry => entry.Name == "Delay");
            Assert.That(type.MemberMatches(delay, "ActionKit Delay"), Is.True);
            Assert.That(type.MemberMatches(delay, "YLFramework.Actions Delay 等待"), Is.True);
            Assert.That(type.Entries.All(entry => !type.MemberMatches(entry, "ActionKit")), Is.True);
            Assert.That(type.Entries.All(entry => !type.MemberMatches(entry, "YLFramework.Actions")), Is.True);
            Assert.That(type.MemberMatches(delay, "ActionKit 不存在的关键词"), Is.False);
        }

        [Test]
        public void MetadataNamesAreOnlyKeysAndNotDisplayedTypeNames()
        {
            foreach (var entry in ActionKitApiHelpContent.BuildIndex())
            {
                StringAssert.DoesNotContain("`", entry.Name);
                StringAssert.DoesNotContain("`", entry.Declaration);
                StringAssert.DoesNotContain("`", entry.Signatures);
            }
        }

        [UnityTest]
        public IEnumerator EveryDisplayedUsageMethodCompilesAgainstTheCurrentRuntime()
        {
            var folder = Path.Combine(Path.GetTempPath(), "YLFramework.Actions.HelpTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var source = Path.Combine(folder, "HelpUsage.cs");
                File.WriteAllText(source, ActionKitApiHelpContent.ExampleCompilationSource());
                var gettingStarted = Path.Combine(folder, "ActionKitQuickStart.cs");
                File.WriteAllText(gettingStarted, ActionKitApiHelpContent.GettingStartedSource);
                var output = Path.Combine(folder, "ActionKit.HelpExamples.dll");
                var builder = new AssemblyBuilder(output, new[] { source, gettingStarted })
                {
                    referencesOptions = ReferencesOptions.UseEngineModules
                };
                var contents = EditorApplication.applicationContentsPath;
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                var allowed = builder.defaultReferences.Where(path => IsEngineOrFrameworkReference(path, contents, ui)).ToArray();
                builder.excludeReferences = builder.defaultReferences.Except(allowed).ToArray();
                builder.additionalReferences = ActionKitApiHelpContent.GetReferenceAssemblies()
                    .Concat(new[] { ui, typeof(YLFramework.Events.EventChannel).Assembly.Location }).Distinct().ToArray();
                CollectionAssert.IsSubsetOf(ActionKitApiHelpContent.GetReferenceAssemblies(), builder.additionalReferences);
                Assert.That(allowed.Any(path => Path.GetFileName(path) == "UnityEngine.CoreModule.dll"), Is.True,
                    "Help example compilation must retain CoreModule; rejected references: " + string.Join("\n", builder.excludeReferences));
                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True);
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }

                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Where(message => message.type == CompilerMessageType.Error).Select(message => message.message), Is.Empty);
                Assert.That(File.Exists(output), Is.True);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        [TestCase("System.Game.dll")]
        [TestCase("UnityEngine.Game.dll")]
        [TestCase("UnityEngine.TestRunner.dll")]
        [TestCase("YLFramework.Runtime.dll")]
        [TestCase("YLFramework.Foundation.Runtime.dll")]
        [TestCase("YLFramework.Utilities.ZipTool.dll")]
        [TestCase("Unrelated.Library.dll")]
        [TestCase("YLFramework.Actions.DOTween.dll")]
        [TestCase("Assembly-CSharp.dll")]
        [TestCase("YLFramework.Tests.EditMode.dll")]
        public void SamePrefixAssembliesOutsideTheUnityInstallationAreNotTrusted(string fileName)
        {
            var folder = Path.Combine(Path.GetTempPath(), "YLFramework.Actions.ReferenceTest-" + Guid.NewGuid().ToString("N"));
            Assert.That(IsEngineOrFrameworkReference(Path.Combine(folder, fileName), EditorApplication.applicationContentsPath,
                typeof(UnityEngine.UI.Image).Assembly.Location), Is.False);
        }

        [Test]
        public void ReferenceFilterRejectsTestAndModuleAssembliesButAcceptsEngineAndExactUiAndYl()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            Assert.That(IsEngineOrFrameworkReference(typeof(LogAssert).Assembly.Location, contents, ui), Is.False);
            Assert.That(IsEngineOrFrameworkReference(typeof(ActionKit).Assembly.Location, contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(typeof(ActionKitHelpProvider).Assembly.Location, contents, ui), Is.False);
            Assert.That(IsEngineOrFrameworkReference(typeof(UnityEngine.Object).Assembly.Location, contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(ui, contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(typeof(YLFramework.Pooling.SafeObjectPool<>).Assembly.Location, contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(typeof(YLFramework.Events.EventChannel).Assembly.Location, contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(typeof(ApiHelpTests).Assembly.Location, contents, ui), Is.False);
        }

        [Test]
        public void ReferenceFilterSupportsTheInstalledStandardAndFrameworkProfilesWithoutPrefixEscape()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            Assert.That(IsEngineOrFrameworkReference(Path.Combine(contents, "NetStandard/ref/2.1.0/netstandard.dll"), contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(Path.Combine(contents, "UnityReferenceAssemblies/unity-4.8-api/mscorlib.dll"), contents, ui), Is.True);
            Assert.That(IsEngineOrFrameworkReference(Path.Combine(contents, "UnityReferenceAssemblies-Fake/System.Game.dll"), contents, ui), Is.False);
        }

        [TestCase("Managed/UnityEngine.dll", true)]
        [TestCase("PlaybackEngines/windowsstandalonesupport/Variations/mono/Managed/UnityEngine.CoreModule.dll", true)]
        [TestCase("PlaybackEngines/windowsstandalonesupport/Variations/il2cpp/Managed/UnityEngine.AudioModule.dll", true)]
        [TestCase("PlaybackEngines/windowsstandalonesupport/Variations/mono/Managed/UnityEngine.TestRunner.dll", false)]
        [TestCase("PlaybackEngines-Fake/UnityEngine.CoreModule.dll", false)]
        [TestCase("PlaybackEngines/../Untrusted/UnityEngine.CoreModule.dll", false)]
        public void ReferenceFilterHandlesTrustedPlayerAndMonolithicEnginePaths(string relativePath, bool expected)
        {
            Assert.That(IsEngineOrFrameworkReference(Path.Combine(EditorApplication.applicationContentsPath, relativePath),
                EditorApplication.applicationContentsPath, typeof(UnityEngine.UI.Image).Assembly.Location), Is.EqualTo(expected));
        }

        [Test]
        public void AssemblyBuilderModuleReferencesSurviveIsolationFiltering()
        {
            var folder = Path.Combine(Path.GetTempPath(), "YLFramework.Actions.ReferenceProbe-" + Guid.NewGuid().ToString("N"));
            var builder = new AssemblyBuilder(Path.Combine(folder, "ReferenceProbe.dll"), new[] { "ReferenceProbe.cs" })
            {
                referencesOptions = ReferencesOptions.UseEngineModules
            };
            var references = builder.defaultReferences;
            Assert.That(builder.flags, Is.EqualTo(AssemblyBuilderFlags.None));
            Assert.That(references.Any(path => Path.GetFileName(path) == "UnityEngine.CoreModule.dll"), Is.True);
            var modules = references.Where(path => Path.GetFileName(path).StartsWith("UnityEngine.", StringComparison.Ordinal) &&
                Path.GetFileName(path) != "UnityEngine.TestRunner.dll").ToArray();
            Assert.That(modules, Is.Not.Empty);
            foreach (var reference in modules)
            {
                Assert.That(IsEngineOrFrameworkReference(reference, EditorApplication.applicationContentsPath,
                    typeof(UnityEngine.UI.Image).Assembly.Location), Is.True, reference);
            }
        }

        internal static bool IsEngineOrFrameworkReference(string path, string unityContents, string uiAssembly)
        {
            var absolute = Path.GetFullPath(path);
            if (string.Equals(absolute, Path.GetFullPath(uiAssembly), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(absolute, Path.GetFullPath(typeof(ActionKit).Assembly.Location), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(absolute, Path.GetFullPath(typeof(YLFramework.Events.EventChannel).Assembly.Location), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var name = Path.GetFileName(absolute);
            if ((name == "UnityEngine.dll" || name.StartsWith("UnityEngine.", StringComparison.Ordinal)) && name != "UnityEngine.TestRunner.dll")
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEngine")) ||
                       IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) ||
                       string.Equals(absolute, Path.GetFullPath(Path.Combine(unityContents, "Managed/UnityEngine.dll")), StringComparison.OrdinalIgnoreCase);
            }

            if (name == "mscorlib.dll" || name == "netstandard.dll" || name == "System.dll" || name.StartsWith("System.", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "NetStandard")) ||
                       IsInsideFolder(absolute, Path.Combine(unityContents, "UnityReferenceAssemblies")) ||
                       IsInsideFolder(absolute, Path.Combine(unityContents, "MonoBleedingEdge/lib/mono"));
            }

            return false;
        }

        private static bool IsInsideFolder(string path, string folder)
        {
            var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        [UnityTest]
        public IEnumerator CoreOnlyQuickStartAndBuiltInGuidesCompileWithNamedAndObservableApis()
        {
            var folder = Path.Combine(Path.GetTempPath(), "YLFramework.Actions.CoreHelpTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var source = Path.Combine(folder, "CoreHelpUsage.cs");
                var topics = ActionKitApiHelpContent.Topics.Where(topic => topic.RequiredAssembly == null && !topic.IsCompleteExample && topic.Example.Length != 0).ToArray();
                CollectionAssert.IsSubsetOf(new[] { "observable", "control" }, topics.Select(topic => topic.Id).ToArray());
                File.WriteAllText(source, "using System;\nusing System.Collections;\nusing System.Threading.Tasks;\nusing UnityEngine;\nusing YLFramework.Actions;\nusing YLFramework.Events;\n" +
                    "namespace ActionKitCoreHelpExamples { public static class Usage {\n" +
                    string.Join("\n\n", topics.Select(topic => topic.Example)) + "\n" +
                    "public static bool NamedUsage(MonoBehaviour owner) {\n" +
                    "ActionHandle handle = ActionKit.Delay(1f, null).StartNamed(owner, \"named\");\n" +
                    "handle.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime); handle.Pause(); handle.Resume();\n" +
                    "ActionKit.TryGetNamed(owner, \"named\", out var found); found.Cancel();\n" +
                    "ActionKit.Delay(1f, null).ReplaceNamed(owner, \"named\", () => {});\n" +
                    "ActionKit.Delay(1f, null).StartNamedCurrentScene(\"scene\");\n" +
                    "ActionKit.Delay(1f, null).ReplaceNamedCurrentScene(\"scene\", _ => {});\n" +
                    "ActionKit.Delay(1f, null).StartNamedGlobal(\"global\");\n" +
                    "ActionKit.Delay(1f, null).ReplaceNamedGlobal(\"global\", () => {});\n" +
                    "ActionKit.TryGetNamedCurrentScene(\"scene\", out _); ActionKit.TryGetNamedGlobal(\"global\", out _);\n" +
                    "return handle.IsActive || ActionKit.CancelNamed(owner, \"named\") || ActionKit.PauseNamed(owner, \"named\") ||\n" +
                    "ActionKit.ResumeNamed(owner, \"named\") || ActionKit.ContainsNamed(owner, \"named\") ||\n" +
                    "ActionKit.CancelNamedCurrentScene(\"scene\") || ActionKit.PauseNamedCurrentScene(\"scene\") ||\n" +
                    "ActionKit.ResumeNamedCurrentScene(\"scene\") || ActionKit.ContainsNamedCurrentScene(\"scene\") ||\n" +
                    "ActionKit.CancelNamedGlobal(\"global\") || ActionKit.PauseNamedGlobal(\"global\") ||\n" +
                    "ActionKit.ResumeNamedGlobal(\"global\") || ActionKit.ContainsNamedGlobal(\"global\");\n" +
                    "}\n} }\n");
                var quickStart = Path.Combine(folder, "ActionKitQuickStart.cs");
                File.WriteAllText(quickStart, ActionKitApiHelpContent.GettingStartedSource);
                var output = Path.Combine(folder, "ActionKit.CoreHelpExamples.dll");
                var builder = new AssemblyBuilder(output, new[] { source, quickStart })
                {
                    referencesOptions = ReferencesOptions.UseEngineModules
                };
                var contents = EditorApplication.applicationContentsPath;
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                var allowed = builder.defaultReferences.Where(path => IsEngineOrFrameworkReference(path, contents, ui)).ToArray();
                builder.excludeReferences = builder.defaultReferences.Except(allowed).ToArray();
                builder.additionalReferences = new[] { ui, typeof(ActionKit).Assembly.Location, typeof(YLFramework.Events.EventChannel).Assembly.Location };
                var effective = builder.defaultReferences.Except(builder.excludeReferences).Concat(builder.additionalReferences).Distinct().ToArray();
                Assert.That(effective.All(path => IsEngineOrFrameworkReference(path, contents, ui)), Is.True);
                Assert.That(effective.Any(path => Path.GetFileName(path) == "UnityEngine.CoreModule.dll"), Is.True);
                foreach (var assembly in new[] { typeof(string).Assembly, typeof(StringComparer).Assembly, typeof(Dictionary<,>).Assembly, typeof(HashSet<>).Assembly })
                {
                    Assert.That(IsEngineOrFrameworkReference(assembly.Location, contents, ui), Is.True, assembly.Location);
                }

                Assert.That(effective.Any(path => Path.GetFileName(path) == "mscorlib.dll" || Path.GetFileName(path) == "netstandard.dll"), Is.True);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween" || assembly.GetName().Name == "DOTween"))
                {
                    CollectionAssert.DoesNotContain(effective, assembly.Location);
                }

                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True);
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }

                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Where(message => message.type == CompilerMessageType.Error).Select(message => message.message), Is.Empty);
                Assert.That(File.Exists(output), Is.True);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }
    }
}

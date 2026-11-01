using System;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using YLFramework.Pooling;
using YLFramework.Foundation;
using YLFramework.Async;

namespace YLFramework.Core.Tests
{
    public class CoreIdentityTests
    {
        [Test]
        public void CoreResponsibilitiesHaveOneOwnerAcrossSmallPartialDeclarations()
        {
            const string Root = "Assets/YLFramework/Runtime/Core/";
            foreach (var path in new[] { "Modules/Module.cs", "Modules/ModuleManager.cs", "Modules/SingletonModule.cs",
                "Modules/Requests/StartUpModuleRequest.cs", "MVC/Models/Model.cs", "MVC/Models/ModelManager.cs",
                "MVC/Controllers/Controller.cs", "MVC/Controllers/ControllerManager.cs", "MVC/Views/View.cs",
                "Events/EventManager.cs", "UI/Windows/Panel.cs", "UI/Windows/PanelConfig.cs", "UI/Windows/PanelOverrides.cs",
                "UI/Windows/PanelRule.cs", "UI/Windows/PanelManager.Requests.cs", "UI/Roots/UIRootContext.cs",
                "UI/Input/InputSystemBridge.cs", "UI/Tips/UITipsPresenter.cs", "Attributes/MustCallBaseAttribute.cs" })
            {
                Assert.That(System.IO.File.Exists(Root + path), Is.True, path);
            }

            foreach (var family in new[] {
                new[] { "Modules/Module", "Models", "Controllers", "Views", "Panels" },
                new[] { "MVC/Views/ViewManager", "Registration", "Loading", "Queries" },
                new[] { "UI/Windows/PanelManager", "Loading", "Sorting", "Input" } })
            {
                var paths = new[] { Root + family[0] + ".cs" }.Concat(family.Skip(1).Select(part => Root + family[0] + "." + part + ".cs")).ToArray();
                foreach (var path in paths)
                {
                    Assert.That(System.IO.File.Exists(path), Is.True, path);
                    StringAssert.Contains("partial class " + System.IO.Path.GetFileName(family[0]), System.IO.File.ReadAllText(path));
                    Assert.That(System.IO.File.Exists(path + ".meta"), Is.True, path);
                }
            }
            Assert.That(System.IO.Directory.GetFiles(Root, "*.asmdef", System.IO.SearchOption.AllDirectories), Is.Empty);
            foreach (var retired in new[] { "WindowCatalog", "WindowDefinition", "UIType", "PanelType" })
            {
                Assert.That(typeof(Panel).Assembly.GetType("YLFramework.Core." + retired), Is.Null, retired);
            }
        }

        [Test]
        public void FormalCoreRootPreservesGuidAndExactRuntimeAssemblyOwnership()
        {
            const string Root = "Assets/YLFramework/Runtime/Core";
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(Root), Is.EqualTo("cc6645c11ab57e145b6b167078de0fac"));
            Assert.That(System.IO.File.Exists(Root + "/YLFramework.Core.Runtime.asmdef"), Is.False);
            Assert.That(System.IO.File.Exists(Root + "/YLFramework.Core.Runtime.asmdef.meta"), Is.False);
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/YLFramework/Runtime/YLFramework.Runtime.asmdef"),
                Is.EqualTo("2d319060abc80e3449e84285dd7fb703"));
            var assembly = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor)
                .Single(a => a.name == "YLFramework.Runtime");
            var files = System.IO.Directory.GetFiles("Assets/YLFramework/Runtime", "*.cs", System.IO.SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/')).ToArray();
            Assert.That(assembly.sourceFiles.Select(p => p.Replace('\\', '/')), Is.EquivalentTo(files));
            Assert.That(typeof(Module).Assembly, Is.SameAs(typeof(YLFramework.YLFrameworkRoot).Assembly));
            Assert.That(System.IO.Directory.GetDirectories("Assets/YLFramework/Runtime").Select(System.IO.Path.GetFileName),
                Is.EquivalentTo(new[] { "Foundation", "Core", "Systems", "Utilities" }));
        }

        [Test]
        public void PeripheralToolsHaveOneRuntimeOwnerAndCoreHasNoUtilityAuthority()
        {
            var runtime = typeof(YLFramework.Save.SaveManager).Assembly;
            var core = typeof(Module).Assembly;
            foreach (var name in new[] { "AnimatorExtensions", "BoundsExtensions", "ColorExtensions", "EventTriggerExtensions",
                "LayerMaskExtensions", "QuaternionExtensions", "RandomExtensions", "RayExtensions", "RigidbodyExtensions",
                "SceneExtensions", "TimeExtensions", "TypeExtensions", "Vector2Extensions", "Vector3Extensions" })
            {
                Assert.That(runtime.GetType("YLFramework.Extensions." + name), Is.Not.Null, name);
                Assert.That(core.GetType("YLFramework.Core.Extensions." + name), Is.Null, name);
            }
            foreach (var name in new[] { "GameObjectExtensions", "ComponentExtensions" })
            {
                Assert.That(core.GetType("YLFramework.Core.Extensions." + name), Is.Null);
                Assert.That(runtime.GetType("YLFramework.Extensions." + name), Is.Null);
            }
            Assert.That(runtime.GetType("YLFramework.Utilities.StringTools"), Is.Not.Null);
            Assert.That(core.GetType("YLFramework.Core.StringTools"), Is.Null);
        }

        [Test]
        public void SerializedDictionaryHasOneFoundationOwnerAndLegacyTypesExit()
        {
            var foundation = typeof(CustomAsyncOperation<>).Assembly;
            var core = typeof(Module).Assembly;
            var runtime = typeof(YLFramework.Save.SaveManager).Assembly;
            var dictionary = foundation.GetType("YLFramework.Serialization.SerializedDictionary`2");
            Assert.That(dictionary, Is.Not.Null);
            var products = new[] { foundation, core, runtime };
            foreach (var assembly in products)
            {
                Assert.That(assembly.GetType("YLFramework.Core.SerializedDictionary`2"), Is.Null);
                Assert.That(assembly.GetType("YLFramework.Core.DictionaryItem`2"), Is.Null);
                Assert.That(assembly.GetType("YLFramework.Save.SerializedDictionary`2"), Is.Null);
            }
            var owners = AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                a.GetType("YLFramework.Serialization.SerializedDictionary`2") != null).ToArray();
            Assert.That(owners, Is.EqualTo(new[] { foundation }));
            Assert.That(core, Is.SameAs(foundation));
            Assert.That(runtime, Is.SameAs(foundation));
            Assert.That(foundation.GetReferencedAssemblies().Select(a => a.Name),
                Does.Not.Contain("YLFramework.Foundation.Runtime"));
        }

        [Test]
        public void CoreLegacyPoolExitsWhileFoundationSupportsExplicitDynamicCreationAndCallbacks()
        {
            var core = typeof(Module).Assembly;
            Assert.That(core.GetType("YLFramework.Core.ClassObjectPoolManager"), Is.Null);
            Assert.That(core.GetType("YLFramework.Core.IReuseable"), Is.Null);
            Assert.That(typeof(ObjectPool<>).Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            var type = typeof(System.Text.StringBuilder);
            var loads = 0;
            var returns = 0;
            using var pool = new ObjectPool<object>(() => Activator.CreateInstance(type),
                onAllocate: _ => loads++, reset: value =>
                {
                    ((System.Text.StringBuilder)value).Clear();
                    returns++;
                });
            var first = pool.Allocate();
            ((System.Text.StringBuilder)first).Append("old lease");
            Assert.That(pool.Recycle(first), Is.True);
            var second = pool.Allocate();
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.ToString(), Is.Empty);
            Assert.That(loads, Is.EqualTo(2), "Both cold and cached allocations must run the optional callback.");
            Assert.That(pool.Recycle(second), Is.True);
            Assert.That(returns, Is.EqualTo(2));
            pool.Clear();
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void ModuleHasOneCloseHookAndExplicitShutdownWithoutLegacyExitEntry()
        {
            Assert.That(typeof(Module).GetMethod("OnExit"), Is.Null);
            var hook = typeof(Module).GetMethod("OnClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(hook.IsFamily && hook.IsVirtual, Is.True);
            var shutdown = typeof(ModuleManager).GetMethod("Shutdown");
            Assert.That(shutdown.IsStatic, Is.True);
            Assert.That(shutdown.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(shutdown.GetParameters(), Is.Empty);
        }

        [TestCase(typeof(StartUpModuleRequest))]
        [TestCase(typeof(LoadViewRequest<View>))]
        [TestCase(typeof(LoadPanelRequest<Panel>))]
        public void RequestsBelongToCoreAndInheritYLFoundation(Type requestType)
        {
            Assert.That(requestType.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(requestType.BaseType.GetGenericTypeDefinition(), Is.EqualTo(typeof(CustomAsyncOperation<>)));
            Assert.That(requestType.BaseType.GenericTypeArguments, Is.EqualTo(new[] { requestType }));
            Assert.That(requestType.BaseType.Assembly, Is.SameAs(requestType.Assembly));
        }

        [Test]
        public void CoreMetadataUsesUnifiedRuntimeAndExplicitPlatformDependencies()
        {
            var references = typeof(Module).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.That(references, Does.Not.Contain("YLFramework.Foundation.Runtime"));
            Assert.That(references, Does.Not.Contain("YLFramework.Core.Runtime"));
            Assert.That(references, Does.Not.Contain("YLFramework.Utilities.ZipTool"));
            var platformReferences = new[] { "netstandard", "mscorlib", "System", "System.Core", "Newtonsoft.Json", "BouncyCastle.Cryptography" };
            Assert.That(references.All(name => platformReferences.Contains(name) ||
                name == "UnityEngine.UI" || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor.", StringComparison.Ordinal)), Is.True, string.Join(", ", references));
            Assert.That(references, Does.Contain("UnityEngine.UI"));
            Assert.That(references, Does.Not.Contain("YLFramework.Runtime"));
            Assert.That(references.Any(n => n.EndsWith(".Editor", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void CoreTypesHaveUniqueNamesAcrossCoexistingFrameworkAssemblies()
        {
            var core = typeof(Module).Assembly;
            // Unity emits global MonoScript metadata types, including nested structs.
            var types = core.GetTypes().Where(t => t.Namespace != null &&
                (t.Namespace == "YLFramework.Core" || t.Namespace.StartsWith("YLFramework.Core.", StringComparison.Ordinal))).Where(t =>
            {
                var declaringType = t;
                while (declaringType.DeclaringType != null)
                {
                    declaringType = declaringType.DeclaringType;
                }

                return !declaringType.IsDefined(typeof(CompilerGeneratedAttribute), false);
            }).ToArray();
            Assert.That(types.All(t => t.Namespace != null &&
                (t.Namespace == "YLFramework.Core" || t.Namespace.StartsWith("YLFramework.Core."))), Is.True);
            Assert.That(typeof(YLFramework.Attributes.CustomClassNameGui).Assembly,
                Is.SameAs(typeof(CustomAsyncOperation<>).Assembly));
            Assert.That(core.GetTypes().Any(t => t.Namespace == "YLFramework.Core.Extensions"), Is.False);

            var others = AppDomain.CurrentDomain.GetAssemblies().Where(a => a != core).ToArray();
            foreach (var type in types)
            {
                foreach (var assembly in others)
                {
                    Assert.That(assembly.GetType(type.FullName), Is.Null, type.FullName + " also exists in " + assembly.GetName().Name);
                }
            }
        }
    }
}

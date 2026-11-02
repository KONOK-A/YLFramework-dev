using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Attributes;

namespace YLFramework.Foundation.Tests
{
    public class AttributesTests
    {
        [CustomClassNameGui("Display")]
        private class Named
        {
        }
        private class Derived : Named
        {
        }

        private class DrawAttribute : CustomPropertyGui
        {
            public DrawAttribute()
            {
            }
            public DrawAttribute(bool reload) : base(reload) { }
            public override void OnPropertyGui(SerializedProperty property)
            {
            }
        }

        private class Fields
        {
            [Draw(true)] public int Value;
            [DisableEdit] public int ReadOnly;
        }

        [Test]
        public void DisplayAndRefreshMetadataRemainReadableWithExplicitUsage()
        {
            var name = (CustomClassNameGui)Attribute.GetCustomAttribute(typeof(Named), typeof(CustomClassNameGui));
            Assert.That(name.GetName(), Is.EqualTo("Display"));
            Assert.That(Attribute.GetCustomAttribute(typeof(Derived), typeof(CustomClassNameGui)), Is.Null);
            Assert.That(new CustomClassNameGui(null).GetName(), Is.Null);
            Assert.That(new CustomClassNameGui("").GetName(), Is.Empty);
            Assert.That(new DrawAttribute().ReloadTreeViewOnValueChange, Is.False);
            var draw = (DrawAttribute)Attribute.GetCustomAttribute(typeof(Fields).GetField(nameof(Fields.Value)), typeof(DrawAttribute));
            Assert.That(draw.ReloadTreeViewOnValueChange, Is.True);
            draw.ReloadTreeViewOnValueChange = false;
            Assert.That(draw.ReloadTreeViewOnValueChange, Is.False);
            AssertUsage(typeof(CustomClassNameGui), AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, false);
            AssertUsage(typeof(CustomPropertyGui), AttributeTargets.Field, false);
            AssertUsage(typeof(DisableEdit), AttributeTargets.Field | AttributeTargets.Property, true);
            Assert.That(Attribute.GetCustomAttribute(typeof(Fields).GetField(nameof(Fields.ReadOnly)), typeof(DisableEdit)), Is.Not.Null);
            Assert.That(typeof(CustomPropertyGui).GetMethod("OnPropertyGui").IsAbstract, Is.True);
        }

        private static void AssertUsage(Type type, AttributeTargets targets, bool inherited)
        {
            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute));
            Assert.That(usage.ValidOn, Is.EqualTo(targets));
            Assert.That(usage.AllowMultiple, Is.False);
            Assert.That(usage.Inherited, Is.EqualTo(inherited));
        }

        [UnityTest]
        public IEnumerator MetadataAndDerivedConstructorsCompileInEditorSourceProfile()
        {
            return Compile(AssemblyBuilderFlags.EditorAssembly);
        }

        [UnityTest]
        public IEnumerator MetadataAndDerivedConstructorsCompileWithoutEditorReferences()
        {
            return Compile(AssemblyBuilderFlags.DevelopmentBuild);
        }

        private static IEnumerator Compile(AssemblyBuilderFlags flags)
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Attributes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                string probe = Path.Combine(folder, "Probe.cs");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(probe), @"using YLFramework.Attributes;
[CustomClassNameGui(""Player metadata"")] public class Probe {
    [Draw(ReloadTreeViewOnValueChange = true), DisableEdit] public int value;
    public bool Read() { return new DrawAttribute(true).ReloadTreeViewOnValueChange && new CustomClassNameGui(""name"").GetName() == ""name""; }
}
public class DrawAttribute : CustomPropertyGui {
    public DrawAttribute() : base() { }
    public DrawAttribute(bool reload) : base(reload) { }
#if UNITY_EDITOR
    public override void OnPropertyGui(UnityEditor.SerializedProperty property) { }
#endif
}");
                string root = global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/Runtime/Foundation/Attributes");
                string output = Path.Combine(folder, "Metadata.dll");
                var builder = new AssemblyBuilder(output, Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.cs").Concat(new[] { probe }).ToArray())
                {
                    flags = flags,
                    buildTarget = BuildTarget.StandaloneWindows64,
                    buildTargetGroup = BuildTargetGroup.Standalone,
                    referencesOptions = ReferencesOptions.UseEngineModules
                };
                bool editor = flags == AssemblyBuilderFlags.EditorAssembly;
                Assert.That(builder.defaultDefines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                // 只使用 Unity 安装目录中的参考程序集，禁止借用项目已编译的 Foundation/Editor 实现。
                string contents = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(EditorApplication.applicationContentsPath)) + Path.DirectorySeparatorChar;
                builder.excludeReferences = builder.defaultReferences.Where(path =>
                    !Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)).StartsWith(contents, StringComparison.OrdinalIgnoreCase) ||
                    (!editor && Path.GetFileName(path).StartsWith("UnityEditor", StringComparison.Ordinal))).ToArray();
                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True);
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }

                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Where(m => m.type == CompilerMessageType.Error).Select(m => m.message), Is.Empty);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.True);
            }
            finally
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
                }
            }
        }
    }
}

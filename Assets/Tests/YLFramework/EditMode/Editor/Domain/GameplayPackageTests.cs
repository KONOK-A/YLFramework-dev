using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Skill;

namespace YLFramework.Tests
{
    [Category("YL.Module.Domain")]
    public class GameplayPackageTests
    {
        [TestCase(typeof(SkillsConfig), "YLFramework.Skill")]
        [TestCase(typeof(BuffsConfig), "YLFramework.Buff")]
        [TestCase(typeof(MissionsConfig), "YLFramework.Mission")]
        [TestCase(typeof(EquipmentsConfig), "YLFramework.Equipment")]
        public void DomainAssetsUseIndependentRuntimeOwnersWithoutMutualReferences(Type type, string owner)
        {
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo(owner));
            string[] domains = { "YLFramework.Skill", "YLFramework.Buff", "YLFramework.Mission", "YLFramework.Equipment" };
            Assert.That(type.Assembly.GetReferencedAssemblies().Where(reference => domains.Contains(reference.Name)), Is.Empty);
        }

        [Test]
        public void AuthoringAndExitHooksShareOneEditorWithoutPublishingInternalHelpers()
        {
            var editor = typeof(YLFramework.Editor.Skill.SkillAuthoring).Assembly;
            Assert.That(editor.GetName().Name, Is.EqualTo("YLFramework.Gameplay.Editor"));
            Assert.That(typeof(YLFramework.Editor.Buff.BuffAuthoring).Assembly, Is.SameAs(editor));
            Assert.That(typeof(YLFramework.Editor.Mission.MissionAuthoring).Assembly, Is.SameAs(editor));
            Assert.That(typeof(YLFramework.Editor.Equipment.EquipmentAuthoring).Assembly, Is.SameAs(editor));
            foreach (var name in new[] { "YLFramework.Editor.Domain.DomainAssetOwnership", "YLFramework.Editor.Domain.DomainResourceIconGui",
                "YLFramework.Editor.Domain.DomainSessionLifecycle", "YLFramework.Editor.Equipment.EquipmentSessionLifecycle" })
            {
                var type = editor.GetType(name);
                Assert.That(type, Is.Not.Null, name);
                Assert.That(type.IsVisible, Is.False, name);
            }
        }

        [TestCase("DomainAssetOwnership", "1ddd9cd019957054cbe3961a7ca6d7b5")]
        [TestCase("DomainResourceIconGui", "95449e78762f9234296438a98b933042")]
        [TestCase("DomainSessionLifecycle", "5005498a231ad034cba239967420cf2e")]
        public void SharedHelpersKeepTheirInternalSingleOwnerAndOriginalGuid(string name, string guid)
        {
            string path = "Packages/com.kano.ylframework.gameplay/Editor/Shared/" + name + ".cs";
            var owner = typeof(YLFramework.Editor.Skill.SkillsConfigController).Assembly;
            var type = owner.GetType("YLFramework.Editor.Domain." + name, true);
            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path).GetClass(), Is.SameAs(type));
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetType(type.FullName) != null), Is.EqualTo(1));
        }

        [Test]
        public void EditorTypesHaveNoRuntimeNamespaceAliases()
        {
            var owner = typeof(YLFramework.Editor.Skill.SkillsConfigController).Assembly;
            Assert.That(owner.GetType("YLFramework.Editor.Skill.SkillsConfigController"), Is.Not.Null);
            Assert.That(owner.GetType("YLFramework.Skill.SkillsConfigController"), Is.Null);
        }

    }
}

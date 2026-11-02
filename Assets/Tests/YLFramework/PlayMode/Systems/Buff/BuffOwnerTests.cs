using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Buff;

namespace YLFramework.Tests.Extensions.Domain
{
    public class BuffOwnerTests
    {
        [SetUp] public void Isolated() => Assert.That(BuffManager.ConfigCount, Is.Zero, "只在无既有领域配置的验收场景运行。");
        private sealed class Host : IAddableBuff
        {
            public List<BuffBase> Buffs { get; } = new();
            public int Added;
            public int Removed;
            public bool CanAdd(int id) => true;
            public void OnAddBuff(BuffBase buff) => Added++;
            public void OnRemoveBuff(BuffBase buff)
            {
                Removed++;
                buff.Remove();
            }
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class Probe : BuffBase
        {
        }

        [Test]
        public void AdditiveSingleAndLastEffectCleanupKeepDefinitionsUnchanged()
        {
            var root = ScriptableObject.CreateInstance<BuffsConfig>();
            var data = ScriptableObject.CreateInstance<BuffConfigData>();
            var effect = ScriptableObject.CreateInstance<EffectData>();
            try
            {
                root.name = "Domain.Buff.Config";
                data.id = 100;
                data.buffClassFullName = typeof(Probe).FullName;
                effect.id = 10;
                data.effects.Add(effect);
                root.buffs.Add(data);
                BuffManager.AddConfig(root);
                var host = new Host();
                var first = BuffManager.AddBuff(host, 100);
                var second = BuffManager.AddBuff(host, 100);
                Assert.That(host.Buffs.Count, Is.EqualTo(2));
                Assert.That(first.BuffData.effects[0], Is.Not.SameAs(effect));
                first.BuffData.effects[0].description = "runtime-only";
                Assert.That(effect.description, Is.Not.EqualTo("runtime-only"));
                Assert.That(second.BuffData.effects[0].description, Is.Not.EqualTo("runtime-only"));
                BuffManager.RemoveBuff(host, first);
                Assert.That(BuffManager.ContainsEffect(host, 10), Is.True);
                BuffManager.CloseHost(host);
                Assert.That(BuffManager.ContainsEffect(host, 10), Is.False);
                Assert.That(host.Buffs, Is.Empty);
                Assert.That(first.Owner, Is.Null);
                Assert.That(host.Added, Is.EqualTo(2));
                Assert.That(host.Removed, Is.EqualTo(2));
                data.mode = BuffMode.Single;
                BuffManager.AddBuff(host, 100);
                BuffManager.AddBuff(host, 100);
                Assert.That(host.Buffs.Count, Is.EqualTo(1));
                BuffManager.CloseHost(host);
                BuffManager.RemoveConfig(root);
                data.buffClassFullName = "Missing.Buff.Type";
                Assert.That(() => BuffManager.AddConfig(root), Throws.InvalidOperationException.With.Message.Contains("100"));
            }
            finally
            {
                BuffManager.Shutdown();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(effect);
            }
        }
    }
}

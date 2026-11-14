using System.Collections.Generic;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Pooling;
using Object = UnityEngine.Object;

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
            public Action<Probe> Tick;
            public override void OnLateUpdate() => Tick?.Invoke(this);
            public override void OnRemove() => Tick = null;
        }

        private static void Dispatch()
        {
            try
            {
                typeof(BuffManager).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { 2 });
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        [Test, Category("YL.Module.Domain")]
        public void NestedMutationAndFailurePreserveDispatchSnapshots()
        {
            var root = ScriptableObject.CreateInstance<BuffsConfig>();
            var data = ScriptableObject.CreateInstance<BuffConfigData>();
            try
            {
                root.name = "Domain.Buff.Dispatch";
                data.id = 100;
                data.duration = 100;
                data.buffClassFullName = typeof(Probe).FullName;
                root.buffs.Add(data);
                BuffManager.AddConfig(root);
                var host = new Host();
                var first = (Probe)BuffManager.AddBuff(host, 100);
                var peer = (Probe)BuffManager.AddBuff(host, 100);
                int firstCalls = 0;
                int peerCalls = 0;
                bool nested = false;
                first.Tick = _ => { firstCalls++; if (!nested) { nested = true; Dispatch(); } };
                peer.Tick = _ => peerCalls++;
                Dispatch();
                Assert.That(firstCalls, Is.EqualTo(2));
                Assert.That(peerCalls, Is.EqualTo(2));
                Probe replacement = null;
                int replacementCalls = 0;
                first.Tick = _ =>
                {
                    first.Tick = null;
                    BuffManager.RemoveBuff(host, peer);
                    replacement = (Probe)BuffManager.AddBuff(host, 100);
                    replacement.Tick = __ => replacementCalls++;
                };
                Dispatch();
                Assert.That(replacement, Is.Not.SameAs(peer));
                Assert.That(peerCalls, Is.EqualTo(2));
                Assert.That(replacementCalls, Is.Zero);
                var hosts = ListPool<IAddableBuff>.Allocate();
                var buffs = ListPool<(BuffBase buff, int version)>.Allocate();
                hosts.RecycleToPool();
                buffs.RecycleToPool();
                var error = new InvalidOperationException("buff-dispatch-marker");
                first.Tick = _ => throw error;
                Assert.That(Assert.Throws<InvalidOperationException>(Dispatch), Is.SameAs(error));
                var returnedHosts = ListPool<IAddableBuff>.Allocate();
                var returnedBuffs = ListPool<(BuffBase buff, int version)>.Allocate();
                Assert.That(returnedHosts, Is.SameAs(hosts));
                Assert.That(returnedBuffs, Is.SameAs(buffs));
                returnedHosts.RecycleToPool();
                returnedBuffs.RecycleToPool();
                first.Tick = _ => BuffManager.Shutdown();
                Dispatch();
                Assert.That(replacementCalls, Is.Zero);
                Assert.That(host.Buffs, Is.Empty);
                Assert.That(BuffManager.HostCount, Is.Zero);
                Assert.That(first.Owner, Is.Null);
            }
            finally
            {
                BuffManager.Shutdown();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(data);
            }
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

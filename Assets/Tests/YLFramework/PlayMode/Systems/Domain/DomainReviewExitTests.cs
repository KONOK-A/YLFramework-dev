using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Resource;
using YLFramework.Skill;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Domain
{
    public class DomainReviewExitTests
    {
        private sealed class SkillHost : IEquippableSkill
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public bool CanRelease() => true;
        }
        private sealed class BuffHost : IAddableBuff
        {
            public List<BuffBase> Buffs { get; } = new();
            public bool CanAdd(int id) => true;
            public void OnAddBuff(BuffBase buff)
            {
            }
            public void OnRemoveBuff(BuffBase buff)
            {
            }
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class ThrowingSkill : SkillBase
        {
            protected internal override void OnRemove() => throw new InvalidOperationException("skill-remove-marker");
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class ThrowingBuff : BuffBase
        {
            public override void OnRemove() => throw new InvalidOperationException("buff-remove-marker");
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class CloseHostBuff : BuffBase
        {
            public override void OnRemove() => BuffManager.CloseHost(Owner);
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class BuddyBuff : BuffBase
        {
            public override void OnRemove()
            {
                if (Owner.Buffs.Count != 0)
                {
                    BuffManager.RemoveBuff(Owner, Owner.Buffs[0]);
                }
            }
        }
        private sealed class Source : IResourceSource
        {
            public ResourceSourceId Id => new("domain-review");
            public bool SupportsSynchronousLoad => true;
            internal Object Asset;
            internal int Releases;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Asset);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException();
            public void Release(Object asset) => Releases++;
        }
        private readonly List<Object> _owned = new();
        private ResourceSystem _resources;
        private bool _isActive;
        [SetUp]
        public void Setup()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount, Is.Zero);
            _isActive = true;
        }
        [TearDown]
        public void Cleanup()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            try
            {
                SkillManager.Shutdown();
            }
            finally
            {
                try
                {
                    BuffManager.Shutdown();
                }
                finally
                {
                    if (_resources != null)
                    {
                        using var stopped = _resources.ShutdownAsync();
                        Assert.That(stopped.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                    }
                    foreach (var asset in _owned)
                    {
                        if (asset)
                        {
                            Object.DestroyImmediate(asset);
                        }
                    }

                    _owned.Clear();
                    _resources = null;
                }
            }
        }
        private T Own<T>() where T : ScriptableObject
        {
            var value = ScriptableObject.CreateInstance<T>();
            _owned.Add(value);
            return value;
        }
        private Source Resource(Object asset, Type type)
        {
            var source = new Source { Asset = asset };
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("domain-review", new[]
            { new ResourceCatalogEntry("root", new ResourceAddress("domain-review", "root"), new ResourceLocation(source.Id, "root", type)) }) }, new IResourceSource[] { source }));
            _resources = initialized.TakeResult();
            return source;
        }

        [Test]
        public void SkillConfigExitContinuesAcrossOwnersAfterRemoveExceptionsAndReturnsLease()
        {
            var root = Own<SkillsConfig>();
            root.name = "Domain.Review.Skill";
            var data = Own<SkillConfigData>();
            data.id = 100;
            data.skillClassFullName = typeof(ThrowingSkill).FullName;
            root.skills.Add(data);
            var source = Resource(root, typeof(SkillsConfig));
            SkillManager.LoadConfig(_resources, new ResourceAddress("domain-review", "root"));
            var first = new SkillHost();
            var second = new SkillHost();
            var a = SkillManager.AddSkills(first, 100);
            SkillManager.AddSkills(second, 100);
            a.Interrupted = _ => Assert.That(() => SkillManager.AddSkills(first, 100), Throws.InvalidOperationException);
            a.Release();
            Assert.Throws<AggregateException>(() => SkillManager.RemoveConfig(root));
            Assert.That(first.Skills, Is.Empty);
            Assert.That(second.Skills, Is.Empty);
            Assert.That(SkillManager.ConfigCount + SkillManager.HostCount, Is.Zero);
            Assert.That(source.Releases, Is.EqualTo(1));
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void BuffConfigExitContinuesAcrossOwnersAfterRemoveExceptionsAndReturnsLease()
        {
            var root = Own<BuffsConfig>();
            root.name = "Domain.Review.Buff";
            var data = Own<BuffConfigData>();
            data.id = 100;
            data.buffClassFullName = typeof(ThrowingBuff).FullName;
            root.buffs.Add(data);
            var source = Resource(root, typeof(BuffsConfig));
            BuffManager.LoadConfig(_resources, new ResourceAddress("domain-review", "root"));
            var first = new BuffHost();
            var second = new BuffHost();
            BuffManager.AddBuff(first, 100);
            BuffManager.AddBuff(second, 100);
            Assert.Throws<AggregateException>(() => BuffManager.RemoveConfig(root));
            Assert.That(first.Buffs, Is.Empty);
            Assert.That(second.Buffs, Is.Empty);
            Assert.That(BuffManager.ConfigCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
            Assert.That(source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void SingleReplacementCannotResurrectHostClosedByRemoveCallback()
        {
            var root = Own<BuffsConfig>();
            root.name = "Domain.Review.Single";
            var data = Own<BuffConfigData>();
            data.id = 100;
            data.mode = BuffMode.Single;
            data.buffClassFullName = typeof(CloseHostBuff).FullName;
            root.buffs.Add(data);
            BuffManager.AddConfig(root);
            var host = new BuffHost();
            BuffManager.AddBuff(host, 100);
            Assert.That(BuffManager.AddBuff(host, 100), Is.Null);
            Assert.That(host.Buffs, Is.Empty);
            Assert.That(BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
        }

        [Test]
        public void AdditiveBatchRemovalSurvivesBuddyRemovalAndClearedSnapshotEntries()
        {
            var root = Own<BuffsConfig>();
            root.name = "Domain.Review.Additive";
            var data = Own<BuffConfigData>();
            data.id = 100;
            data.buffClassFullName = typeof(BuddyBuff).FullName;
            root.buffs.Add(data);
            BuffManager.AddConfig(root);
            var host = new BuffHost();
            BuffManager.AddBuff(host, 100);
            BuffManager.AddBuff(host, 100);
            Assert.DoesNotThrow(() => BuffManager.RemoveBuff(host, 100));
            Assert.That(host.Buffs, Is.Empty);
            Assert.That(BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
        }
    }
}

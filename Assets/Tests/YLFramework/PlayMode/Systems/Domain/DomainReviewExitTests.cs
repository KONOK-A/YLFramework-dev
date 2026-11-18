using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Buff;
using YLFramework.Resource;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Resource.Addressables.Regression")]
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
        private AddressablesDomainResourceFixture _fixture;
        private bool _isActive;
        [UnitySetUp]
        public IEnumerator Setup()
        {
            _isActive = false;
            _fixture = null;
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount, Is.Zero);
            _isActive = true;
            _fixture = new AddressablesDomainResourceFixture();
            yield return _fixture.Start();
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (!_isActive)
            {
                yield break;
            }

            _isActive = false;
            var errors = new List<Exception>();
            foreach (Action close in new Action[] { SkillManager.Shutdown, BuffManager.Shutdown })
            {
                try
                {
                    close();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            if (_fixture != null)
                yield return _fixture.Close();
            if (errors.Count != 0)
                throw new AggregateException(errors);
        }
        private T Own<T>() where T : ScriptableObject => _fixture.New<T>();

        [UnityTest]
        public IEnumerator SkillConfigExitContinuesAcrossOwnersAfterRemoveExceptionsAndReturnsLease()
        {
            var root = Own<SkillsConfig>();
            root.name = "Domain.Review.Skill";
            var data = Own<SkillConfigData>();
            data.id = 100;
            data.parent = root;
            data.skillClassFullName = typeof(ThrowingSkill).FullName;
            root.skills.Add(data);
            _fixture.Add("root", root);
            var load = _fixture.Track(SkillManager.LoadConfigAsync(_fixture.Resources, _fixture.Address("root")));
            yield return AddressablesDomainResourceFixture.Wait(() => load.IsCompleted, "throwing skill config load");
            Assert.That(load.IsCompletedSuccessfully, Is.True, load.Exception?.ToString());
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
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1 && AddressablesDomainResourceFixture.IsQuiescent(_fixture.Resources), "throwing skill root cleanup");
            AddressablesDomainResourceFixture.AssertQuiescent(_fixture.Resources);
        }

        [UnityTest]
        public IEnumerator BuffConfigExitContinuesAcrossOwnersAfterRemoveExceptionsAndReturnsLease()
        {
            var root = Own<BuffsConfig>();
            root.name = "Domain.Review.Buff";
            var data = Own<BuffConfigData>();
            data.id = 100;
            data.parent = root;
            data.buffClassFullName = typeof(ThrowingBuff).FullName;
            root.buffs.Add(data);
            _fixture.Add("root", root);
            var load = _fixture.Track(BuffManager.LoadConfigAsync(_fixture.Resources, _fixture.Address("root")));
            yield return AddressablesDomainResourceFixture.Wait(() => load.IsCompleted, "throwing buff config load");
            Assert.That(load.IsCompletedSuccessfully, Is.True, load.Exception?.ToString());
            var first = new BuffHost();
            var second = new BuffHost();
            BuffManager.AddBuff(first, 100);
            BuffManager.AddBuff(second, 100);
            Assert.Throws<AggregateException>(() => BuffManager.RemoveConfig(root));
            Assert.That(first.Buffs, Is.Empty);
            Assert.That(second.Buffs, Is.Empty);
            Assert.That(BuffManager.ConfigCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1 && AddressablesDomainResourceFixture.IsQuiescent(_fixture.Resources), "throwing buff root cleanup");
            AddressablesDomainResourceFixture.AssertQuiescent(_fixture.Resources);
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

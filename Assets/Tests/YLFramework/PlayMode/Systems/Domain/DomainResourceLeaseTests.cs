using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Resource;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Resource.Addressables.Regression")]
    public class DomainResourceLeaseTests
    {
        private AddressablesDomainResourceFixture _fixture;
        private bool _ownsManagers;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownsManagers = false;
            _fixture = null;
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero);
            _ownsManagers = true;
            _fixture = new AddressablesDomainResourceFixture();
            yield return _fixture.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsManagers)
                yield break;
            _ownsManagers = false;
            SkillManager.Shutdown();
            BuffManager.Shutdown();
            MissionManager.Shutdown();
            if (_fixture != null)
                yield return _fixture.Close();
        }

        [UnityTest]
        public IEnumerator ManagersOwnOnlyTheirRootLeasesAndReturnThemOnShutdown()
        {
            var skill = _fixture.New<SkillsConfig>("skill");
            var buff = _fixture.New<BuffsConfig>("buff");
            var mission = _fixture.New<MissionsConfig>("mission");
            var system = _fixture.Resources;
            using var request = system.AcquireAsync<SkillsConfig>(_fixture.Key("skill"));
            yield return AddressablesDomainResourceFixture.Wait(() => request.IsDone, "independent skill root");
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var independent = _fixture.Hold(request.TakeResult());
            SkillManager.LoadConfig(system, _fixture.Address("skill"));
            SkillManager.LoadConfig(system, _fixture.Address("skill"));
            var buffLoad = _fixture.Track(BuffManager.LoadConfigAsync(system, _fixture.Address("buff")));
            var missionLoad = _fixture.Track(MissionManager.LoadConfigAsync(system, _fixture.Address("mission")));
            yield return AddressablesDomainResourceFixture.Wait(() => buffLoad.IsCompleted && missionLoad.IsCompleted, "domain root registration");
            Assert.That(buffLoad.IsCompletedSuccessfully, Is.True, buffLoad.Exception?.ToString());
            Assert.That(missionLoad.IsCompletedSuccessfully, Is.True, missionLoad.Exception?.ToString());
            Assert.That(BuffManager.GetConfig(buff.name), Is.SameAs(buff));
            Assert.That(MissionManager.GetConfig(mission.name), Is.SameAs(mission));
            Assert.That(system.GetDiagnostics().LeaseCount, Is.EqualTo(4), "Repeated registration must not retain an extra manager lease.");
            SkillManager.Shutdown();
            Assert.That(SkillManager.GetConfig(skill.name), Is.Null);
            Assert.That(independent.Asset, Is.SameAs(skill));
            Assert.That(_fixture.Provider.Releases, Is.Zero, "An independent lease still owns the skill root.");
            independent.Dispose();
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "independent skill release");
            BuffManager.Shutdown();
            MissionManager.Shutdown();
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 3 && AddressablesDomainResourceFixture.IsQuiescent(system), "domain root cleanup");
            AddressablesDomainResourceFixture.AssertQuiescent(system);
            Assert.That(ResourceSystem.Current, Is.SameAs(system), "Domain shutdown must not stop the resource authority.");
            Assert.That(system.IsAvailable, Is.True);
        }
    }
}

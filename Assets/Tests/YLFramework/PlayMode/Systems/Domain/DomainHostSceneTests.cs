using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Buff;

namespace YLFramework.Tests.Extensions.Domain
{
    public sealed class DomainBuffOwnerBehaviour : MonoBehaviour, IAddableBuff
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

    public class DomainHostSceneTests
    {
        private Scene _scene;
        private BuffsConfig _root;
        private BuffConfigData _data;
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root)
            {
                BuffManager.RemoveConfig(_root);
            }

            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }

            if (_root)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }

            if (_data)
            {
                UnityEngine.Object.DestroyImmediate(_data);
            }
        }

        [UnityTest]
        public IEnumerator InactivePausesDurationAndSceneUnloadReturnsDestroyedHost()
        {
            Assert.That(BuffManager.ConfigCount, Is.Zero, "只在隔离验收场景运行，不接管已登记领域配置。");
            Assert.That(Time.timeScale, Is.EqualTo(1), "验收owner应使用正常timeScale，本用例不改全局时钟。");
            _root = ScriptableObject.CreateInstance<BuffsConfig>();
            _root.name = "Domain.Scene.Config";
            _data = ScriptableObject.CreateInstance<BuffConfigData>();
            _data.id = 100;
            _data.duration = 0.03f;
            _data.buffClassFullName = typeof(BuffBase).FullName;
            _root.buffs.Add(_data);
            BuffManager.AddConfig(_root);
            _scene = SceneManager.CreateScene("DomainScene-" + Guid.NewGuid().ToString("N"));
            var obj = new GameObject("Domain.Scene.Owner");
            SceneManager.MoveGameObjectToScene(obj, _scene);
            var host = obj.AddComponent<DomainBuffOwnerBehaviour>();
            var timed = BuffManager.AddBuff(host, 100);
            obj.SetActive(false);
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(timed.ExistedTime, Is.Zero);
            obj.SetActive(true);
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(host.Buffs, Is.Empty);
            Assert.That(timed.Owner, Is.Null);
            var permanent = BuffManager.AddBuff(host, 100, 0f);
            yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;
            yield return null;
            Assert.That(permanent.Owner, Is.Null);
            Assert.That(BuffManager.HostCount, Is.Zero);
            Assert.That(BuffManager.EffectOwnerCount, Is.Zero);
        }
    }
}

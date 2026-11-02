#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Events;
using EventType = YLFramework.Events.EventType;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.CommonSystem
{
    public class UnityPhysicsListenerPlayModeTests
    {
        private Scene _scene;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
        }

        private GameObject Root(string name)
        {
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, _scene);
            return root;
        }

        [UnityTest] public IEnumerator NativeCollision3D() => Matrix(false, false);
        [UnityTest] public IEnumerator NativeTrigger3D() => Matrix(false, true);
        [UnityTest] public IEnumerator NativeCollision2D() => Matrix(true, false);
        [UnityTest] public IEnumerator NativeTrigger2D() => Matrix(true, true);

        private IEnumerator Matrix(bool twoD, bool trigger)
        {
            _scene = SceneManager.CreateScene("YL.CommonSystem.Physics",
                new CreateSceneParameters(twoD ? LocalPhysicsMode.Physics2D : LocalPhysicsMode.Physics3D));
            var mask = typeof(EventListener).GetProperty("PhysicsLayerMask");
            Assert.That(mask, Is.Not.Null);
            // 每种掩码用独立接触对，避免重复瞬移的 2D 接触历史影响 Enter/Exit 证据。
            foreach (var value in new[] { Physics.AllLayers, 0, 1 << 31, 1 })
            {
                var host = Root("Listener");
                var other = Root("Other layer zero");
                var flag = Root("Persistent receiver");
                host.transform.position = Vector3.right * 10;
                var listener = host.AddComponent<EventListener>();
                var counts = new int[3];
                var trace = new List<string>();
                Action<Vector3> move;
                Action step;
                if (twoD)
                {
                    Assert.That(Physics2D.GetIgnoreLayerCollision(0, 0), Is.False, "The protected project matrix must allow layer zero.");
                    host.AddComponent<BoxCollider2D>().isTrigger = trigger;
                    var body = host.AddComponent<Rigidbody2D>();
                    body.gravityScale = 0;
                    body.constraints = RigidbodyConstraints2D.FreezeAll;
                    body.sleepMode = RigidbodySleepMode2D.NeverSleep;
                    other.AddComponent<BoxCollider2D>();
                    move = position => body.position = position;
                    step = () => Assert.That(_scene.GetPhysicsScene2D().Simulate(0.02f), Is.True);
                    if (trigger)
                    {
                        Wire<Collider2D>(listener, true, true, counts, flag, payload => payload.gameObject, other);
                    }
                    else
                    {
                        Wire<Collision2D>(listener, true, false, counts, flag, payload => payload.gameObject, other);
                    }
                }
                else
                {
                    Assert.That(Physics.GetIgnoreLayerCollision(0, 0), Is.False, "The protected project matrix must allow layer zero.");
                    host.AddComponent<BoxCollider>().isTrigger = trigger;
                    var body = host.AddComponent<Rigidbody>();
                    body.useGravity = false;
                    body.constraints = RigidbodyConstraints.FreezeAll;
                    body.sleepThreshold = 0;
                    other.AddComponent<BoxCollider>();
                    move = position => body.position = position;
                    step = () =>
                    {
                        body.WakeUp();
                        _scene.GetPhysicsScene().Simulate(0.02f);
                    };
                    if (trigger)
                    {
                        Wire<Collider>(listener, false, true, counts, flag, payload => payload.gameObject, other);
                    }
                    else
                    {
                        Wire<Collision>(listener, false, false, counts, flag, payload => payload.gameObject, other);
                    }
                }
                mask.SetValue(listener, (LayerMask)value);
                var accepted = value == Physics.AllLayers || value == 1;
                for (var phase = 0; phase < 3; phase++)
                {
                    flag.SetActive(true);
                    var before = counts[phase];
                    move(phase == 2 ? Vector3.right * 10 : Vector3.zero);
                    step();
                    yield return null;
                    trace.Add($"mask={value}, phase={phase}, pos={host.transform.position}, counts=" + string.Join(",", counts));
                    Assert.That(counts[phase] - before, Is.EqualTo(accepted ? 1 : 0), string.Join("; ", trace));
                    Assert.That(flag.activeSelf, Is.EqualTo(!accepted), $"Inspector mask={value}, phase={phase}");
                }
                Object.Destroy(host);
                Object.Destroy(other);
                Object.Destroy(flag);
                yield return null;
            }
        }

        private static void Wire<T>(EventListener listener, bool twoD, bool trigger, int[] counts,
            GameObject flag, Func<T, GameObject> target, GameObject other)
        {
            var prefix = trigger ? "Trigger" : "Collision";
            var suffix = twoD ? "2D" : "";
            var phases = new[] { "Enter", "Stay", "Exit" };
            for (var i = 0; i < phases.Length; i++)
            {
                var index = i;
                var type = (EventType)Enum.Parse(typeof(EventType), "On" + prefix + phases[i] + suffix);
                var unityEvent = new UnityEvent<T>();
                UnityEventTools.AddBoolPersistentListener(unityEvent, flag.SetActive, false);
                typeof(EventListener).GetField(char.ToLowerInvariant(prefix[0]) + prefix.Substring(1) + phases[i] + suffix,
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(listener, unityEvent);
                listener.Register<T>(type, payload =>
                {
                    Assert.That(target(payload), Is.SameAs(other));
                    counts[index]++;
                });
            }
        }
    }
}
#endif

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Extensions;
using YLFramework.Events;

namespace YLFramework.Core.Tests
{
    public class NeutralLayerMaskConsumerTests
    {
        [Test]
        public void AnimationBridgeKeepsItsFieldAndExactStringCallbackContract()
        {
            var host = new GameObject("Animation bridge migration");
            try
            {
                var listener = host.AddComponent<AnimationEventListener>();
                string observed = null;
                UnityEngine.Events.UnityAction<string> callback = value => observed = value;
                listener.animationEvent.AddListener(callback);
                listener.OnAnimationEvent("walk");
                Assert.That(observed, Is.EqualTo("walk"));
                listener.animationEvent.RemoveListener(callback);
                listener.OnAnimationEvent("idle");
                Assert.That(observed, Is.EqualTo("walk"));
                Assert.That(typeof(AnimationEventListener).GetField("animationEvent").FieldType,
                    Is.EqualTo(typeof(YLFramework.Events.AnimationEvent)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RuntimeWrappersAndFoundationKeepValidatedMembershipAndUnityNullSemantics()
        {
            var target = new GameObject("LayerMask consumer");
            try
            {
                foreach (int layer in new[] { 0, 31 })
                {
                    target.layer = layer;
                    LayerMask included = 1 << layer;
                    LayerMask excluded = ~(1 << layer);
                    Assert.That(included.Contains(layer), Is.True);
                    Assert.That(excluded.Contains(layer), Is.False);
                    Assert.That(target.IsInLayerMask(included), Is.True);
                    Assert.That(target.transform.IsInLayerMask(included), Is.True);
                    Assert.That(target.IsInLayerMask(excluded), Is.False);
                    Assert.That(target.transform.IsInLayerMask(excluded), Is.False);
                }
                Assert.That(LayerMaskUtility.IsInLayerMask(-1, -1), Is.False);
                Assert.That(LayerMaskUtility.IsInLayerMask(32, -1), Is.False);
                Assert.That(LayerMaskUtility.IsInLayerMask((GameObject)null, -1), Is.False);
                UnityEngine.Object.DestroyImmediate(target);
                Assert.That(LayerMaskUtility.IsInLayerMask(target, -1), Is.False);
            }
            finally
            {
                if (target)
                {
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
        }

        [Test]
        public void CoreTriggerCallbacksFilterRealThreeAndTwoDimensionalColliderLayers()
        {
            var host = new GameObject("LayerMask listener");
            var other = new GameObject("LayerMask collider");
            var other2D = new GameObject("LayerMask collider 2D");
            try
            {
                var listener = host.AddComponent<TriggerEventListener>();
                var collider = other.AddComponent<BoxCollider>();
                var collider2D = other2D.AddComponent<BoxCollider2D>();
                Assert.That(collider && collider2D, Is.True);
                int calls = 0;
                listener.onTriggerEnter.AddListener(_ => calls++);
                listener.onTriggerStay.AddListener(_ => calls++);
                listener.onTriggerExit.AddListener(_ => calls++);
                listener.onTriggerEnter2D.AddListener(_ => calls++);
                listener.onTriggerStay2D.AddListener(_ => calls++);
                listener.onTriggerExit2D.AddListener(_ => calls++);
                var mask = typeof(TriggerEventListener).GetField("layerMask", BindingFlags.Instance | BindingFlags.NonPublic);
                listener.PhysicsLayerMask = 1 << 31;
                Assert.That(((LayerMask)mask.GetValue(listener)).value, Is.EqualTo(1 << 31));
                foreach (int layer in new[] { 0, 31 })
                {
                    other.layer = layer;
                    other2D.layer = layer;
                    foreach (string name in new[] { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit" })
                    {
                        // EditMode 下不走 Unity 的 Play 行为分发；直接验证真实消费者对 Collider 的过滤。
                        typeof(TriggerEventListener).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(listener, new object[] { collider });
                        typeof(TriggerEventListener).GetMethod(name + "2D", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(listener, new object[] { collider2D });
                    }
                    Assert.That(calls, Is.EqualTo(layer == 0 ? 0 : 6));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(other);
                UnityEngine.Object.DestroyImmediate(other2D);
            }
        }

        [Test]
        public void ExplicitSerializedNoneIsNotReplacedByTheNewDefault()
        {
            var host = new GameObject("LayerMask serialized None");
            try
            {
                var trigger = host.AddComponent<TriggerEventListener>();
                var collision = host.AddComponent<CollisionEventListener>();
                trigger.PhysicsLayerMask = 0;
                collision.PhysicsLayerMask = 0;
                var triggerJson = JsonUtility.ToJson(trigger);
                var collisionJson = JsonUtility.ToJson(collision);
                trigger.PhysicsLayerMask = Physics.AllLayers;
                collision.PhysicsLayerMask = Physics.AllLayers;
                JsonUtility.FromJsonOverwrite(triggerJson, trigger);
                JsonUtility.FromJsonOverwrite(collisionJson, collision);
                Assert.That(trigger.PhysicsLayerMask.value, Is.Zero);
                Assert.That(collision.PhysicsLayerMask.value, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}

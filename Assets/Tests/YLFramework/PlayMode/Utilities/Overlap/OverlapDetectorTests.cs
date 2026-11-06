using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Overlap;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Behavior
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class OverlapDetectorTests
    {
        private readonly bool _threeD;
        private Scene _scene;
        private Rig _rig;
        private readonly List<string> _events = new List<string>();

        public OverlapDetectorTests(bool threeD) => _threeD = threeD;

        [SetUp]
        public void Setup()
        {
            _scene = SceneManager.CreateScene("YL.Detector." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(_threeD ? LocalPhysicsMode.Physics3D : LocalPhysicsMode.Physics2D));
            _rig = NewRig();
            _rig.Entered += (_, id) => _events.Add("Enter:" + id);
            _rig.Stayed += (_, id) => _events.Add("Stay:" + id);
            _rig.Exited += (_, id, reason) => _events.Add("Exit:" + id + ":" + reason);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
            _events.Clear();
        }

        private GameObject NewObject(Vector3 position, Transform parent = null)
        {
            var obj = new GameObject("YL.Detector.Owned");
            SceneManager.MoveGameObjectToScene(obj, _scene);
            obj.transform.position = position;
            obj.transform.SetParent(parent, true);
            return obj;
        }

        private Rig NewRig() => new Rig(NewObject(Vector3.zero), _threeD);

        private Component Target(Vector3 position, Transform parent = null)
        {
            var obj = NewObject(position, parent);
            if (_threeD)
            {
                var collider = obj.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 0.2f;
                return collider;
            }
            var two = obj.AddComponent<BoxCollider2D>();
            two.size = Vector2.one * 0.2f;
            return two;
        }

        private void Sync()
        {
            if (_threeD)
            {
                Physics.SyncTransforms();
            }
            else
            {
                Physics2D.SyncTransforms();
            }
        }

        [Test]
        public void EnterStayExitCommitBeforeCallbacksAndNewContactsDoNotStay()
        {
            var target = Target(Vector3.zero);
            int id = target.GetInstanceID();
            _rig.Entered += (_, __) => Assert.That(_rig.Count, Is.EqualTo(1));
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_events, Is.EqualTo(new[] { "Enter:" + id }));
            _rig.Sample();
            Assert.That(_events[1], Is.EqualTo("Stay:" + id));
            target.transform.position = Vector3.right * 20;
            Sync();
            _rig.Sample();
            Assert.That(_events[2], Is.EqualTo("Exit:" + id + ":Separated"));
            Assert.That(_rig.Count, Is.Zero);
        }

        [TestCase(2)]
        [TestCase(3)]
        public void TruncatedSnapshotDoesNotEmitFalseExit(int denseCount)
        {
            _rig.Capacity(2, 2, false);
            var original = Target(Vector3.zero);
            Sync();
            _rig.Sample();
            original.transform.position = Vector3.up * 20;
            var dense = new List<Component>();
            for (int i = 0; i < denseCount; i++)
            {
                dense.Add(Target(Vector3.zero));
            }
            Sync();
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(_rig.RawCount, Is.EqualTo(2));
            Assert.That(_rig.Count, Is.EqualTo(1));
            Assert.That(_rig.TargetAt(0), Is.SameAs(original));
            Assert.That(_events.Count, Is.EqualTo(1));
            for (int i = 1; i < dense.Count; i++)
            {
                dense[i].gameObject.SetActive(false);
            }
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_events, Is.EqualTo(new[] { "Enter:" + original.GetInstanceID(),
                "Exit:" + original.GetInstanceID() + ":Separated", "Enter:" + dense[0].GetInstanceID() }));
        }

        [Test]
        public void BoundedGrowthRerunsExactFullAndReusesCapacityForSparseSamples()
        {
            _rig.Capacity(2, 8, true);
            for (int i = 0; i < 4; i++)
            {
                Target(Vector3.zero);
            }
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_rig.BufferCapacity, Is.EqualTo(8));
            Assert.That(_rig.Count, Is.EqualTo(4));
            _rig.Component.transform.position = Vector3.up * 30;
            Sync();
            _rig.Sample();
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_rig.BufferCapacity, Is.EqualTo(8));
        }

        [Test]
        public void MaxCapacityAndUnionOverflowCannotCommitEvenWhenEveryChildFits()
        {
            _rig.Capacity(2, 3, true);
            _rig.Shapes(1, new[] { Vector3.left * 2, Vector3.right * 2 });
            Target(Vector3.left * 2);
            Target(Vector3.left * 2 + Vector3.up * 0.3f);
            Target(Vector3.right * 2);
            Target(Vector3.right * 2 + Vector3.up * 0.3f);
            Sync();
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(_rig.BufferCapacity, Is.EqualTo(3));
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void OneTruncatedChildBlocksTheWholeUnionCommit()
        {
            _rig.Capacity(2, 2, false);
            _rig.Shapes(1, new[] { Vector3.left * 2, Vector3.right * 2 });
            Target(Vector3.left * 2);
            Target(Vector3.right * 2);
            Target(Vector3.right * 2 + Vector3.up * 0.1f);
            Sync();
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.RawCount, Is.EqualTo(3));
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void RawSaturationCannotBeHiddenByBodyGrouping()
        {
            var body = NewObject(Vector3.zero);
            if (_threeD)
            {
                body.AddComponent<Rigidbody>().isKinematic = true;
            }
            else
            {
                body.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            }
            Target(Vector3.zero, body.transform);
            Target(Vector3.right * 0.1f, body.transform);
            _rig.Capacity(2, 2, false);
            _rig.Filter(groupByBody: true);
            Sync();
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.RawCount, Is.EqualTo(2));
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(_rig.Count, Is.Zero);
        }

        [Test]
        public void TriggerPolicyExposesEngineInvalidationWithoutDiscardingCompleteContacts()
        {
            bool old = _threeD ? Physics.queriesHitTriggers : Physics2D.queriesHitTriggers;
            var target = Target(Vector3.zero);
            if (_threeD)
            {
                ((Collider)target).isTrigger = true;
            }
            else
            {
                ((Collider2D)target).isTrigger = true;
            }
            try
            {
                SetGlobalTriggers(true);
                _rig.Filter(triggers: true);
                Sync();
                Assert.That(_rig.Sample(), Is.True);
                Assert.That(_rig.Count, Is.EqualTo(1));
                SetGlobalTriggers(false);
                Assert.That(_rig.Sample(), Is.EqualTo(_threeD));
                Assert.That(_rig.Status, Is.EqualTo(_threeD ? OverlapQueryStatus.Complete : OverlapQueryStatus.Invalidated));
                Assert.That(_rig.InvalidationReason, Is.EqualTo(_threeD ? OverlapQueryInvalidationReason.None :
                    OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                Assert.That(_rig.Count, Is.EqualTo(1));
                Assert.That(_events.Count, Is.EqualTo(_threeD ? 2 : 1));
                Assert.That(_threeD ? Physics.queriesHitTriggers : Physics2D.queriesHitTriggers, Is.False);
                SetGlobalTriggers(true);
                _rig.Filter(triggers: false);
                _rig.Sample();
                Assert.That(_rig.Count, Is.Zero);
                Assert.That(_events[_events.Count - 1], Does.EndWith(":FilterChanged"));
            }
            finally
            {
                SetGlobalTriggers(old);
            }
        }

        private void SetGlobalTriggers(bool value)
        {
            if (_threeD)
            {
                Physics.queriesHitTriggers = value;
            }
            else
            {
                Physics2D.queriesHitTriggers = value;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompoundUnionDeduplicates(bool groupByBody)
        {
            var body = NewObject(Vector3.zero);
            if (_threeD)
            {
                body.AddComponent<Rigidbody>().isKinematic = true;
            }
            else
            {
                body.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            }
            Target(Vector3.zero, body.transform);
            Target(Vector3.right * 0.25f, body.transform);
            Target(Vector3.left * 0.25f);
            _rig.Filter(groupByBody: groupByBody);
            _rig.Shapes(1, new[] { Vector3.zero, Vector3.right * 0.1f, Vector3.left * 0.1f });
            Sync();
            _rig.Sample();
            Assert.That(_rig.Count, Is.EqualTo(groupByBody ? 2 : 3));
            Assert.That(_events.Count, Is.EqualTo(groupByBody ? 2 : 3));
            _rig.Sample();
            Assert.That(_events.Count, Is.EqualTo(groupByBody ? 4 : 6));
        }

        [Test]
        public void ReentrantDisableInvalidatesCurrentCycle()
        {
            Target(Vector3.zero);
            Target(Vector3.right * 0.25f);
            int enters = 0;
            _rig.Entered += (_, __) =>
            {
                enters++;
                _rig.Component.enabled = false;
            };
            Sync();
            _rig.Sample();
            Assert.That(enters, Is.EqualTo(1));
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_rig.IsRunning, Is.False);
            Assert.That(_rig.IsSampling, Is.False);
            Assert.That(_events.FindAll(e => e.EndsWith(":Disabled")).Count, Is.EqualTo(2));
            _rig.Component.enabled = true;
            _rig.Start();
            Assert.That(_rig.IsRunning, Is.True);
        }

        [Test]
        public void NestedSampleInvalidatesRemainingEventsButDoesNotCorruptAnotherInstance()
        {
            Target(Vector3.zero);
            Target(Vector3.right * 0.2f);
            var other = NewRig();
            int enters = 0;
            _rig.Entered += (_, __) =>
            {
                enters++;
                Assert.That(_rig.Sample(), Is.False);
                Assert.That(other.Sample(), Is.True);
            };
            Sync();
            _rig.Sample();
            Assert.That(enters, Is.EqualTo(1));
            Assert.That(other.Count, Is.EqualTo(2));
            Assert.That(_rig.Count, Is.EqualTo(2));
            Assert.That(_rig.IsSampling, Is.False);
        }

        [Test]
        public void CallbackCanEnumerateCommittedContactsWhileStoppingWithoutMutatingThatEnumeration()
        {
            Target(Vector3.zero);
            Target(Vector3.right * 0.2f);
            int seen = 0;
            _rig.Entered += (_, __) =>
            {
                foreach (var contact in _rig.Snapshot)
                {
                    if (++seen == 1)
                    {
                        _rig.Stop();
                    }
                }
            };
            Sync();
            Assert.DoesNotThrow(() => _rig.Sample());
            Assert.That(seen, Is.EqualTo(2));
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_rig.IsSampling, Is.False);
        }

        [Test]
        public void ObserverFailurePropagatesAfterCommitAndFinallyRestoresSampling()
        {
            Target(Vector3.zero);
            Action<Object, int> fail = (_, __) => throw new InvalidOperationException("observer");
            _rig.Entered += fail;
            Sync();
            Assert.Throws<InvalidOperationException>(() => _rig.Sample());
            Assert.That(_rig.Count, Is.EqualTo(1));
            Assert.That(_rig.IsSampling, Is.False);
            _rig.Entered -= fail;
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_events.Count, Is.EqualTo(2));
            Assert.That(_events[1], Does.StartWith("Stay:"));
            _rig.Exited += (_, __, ___) => throw new InvalidOperationException("exit observer");
            Assert.Throws<InvalidOperationException>(() => _rig.Stop());
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_rig.IsRunning, Is.False);
            Assert.That(_rig.IsSampling, Is.False);
        }

        [Test]
        public void StopAndPoolReuseHaveExplicitReasonsAndFreshEnters()
        {
            var target = Target(Vector3.zero);
            Sync();
            _rig.Sample();
            _rig.Stop();
            _rig.Stop();
            Assert.That(_events, Is.EqualTo(new[] { "Enter:" + target.GetInstanceID(),
                "Exit:" + target.GetInstanceID() + ":Stopped" }));
            Assert.That(_rig.Sample(), Is.False);
            _rig.Start();
            _rig.Sample();
            _rig.Component.gameObject.SetActive(false);
            Assert.That(_events[3], Does.EndWith(":Disabled"));
            _rig.Component.gameObject.SetActive(true);
            _rig.Sample();
            Assert.That(_events[4], Does.StartWith("Enter:"));
        }

        [Test]
        public void DestroyOwnerDoesNotDuplicateDisabledExits()
        {
            Target(Vector3.zero);
            Sync();
            _rig.Sample();
            Object.DestroyImmediate(_rig.Component.gameObject);
            Assert.That(_events.Count, Is.EqualTo(2));
            Assert.That(_events[1], Does.EndWith(":Disabled"));
        }

        [Test]
        public void DestroyedTargetUsesCachedIdentityWithoutDereferencingFakeNull()
        {
            var target = Target(Vector3.zero);
            int id = target.GetInstanceID();
            Sync();
            _rig.Sample();
            Object.DestroyImmediate(target.gameObject);
            Sync();
            _rig.Sample();
            Assert.That(_events[1], Is.EqualTo("Exit:" + id + ":TargetDestroyed"));
            Assert.That(_rig.Count, Is.Zero);
        }

        [Test]
        public void DisabledTargetAndChangedFilterHaveTruthfulExitReasons()
        {
            var target = Target(Vector3.zero);
            Sync();
            _rig.Sample();
            target.gameObject.SetActive(false);
            Sync();
            _rig.Sample();
            Assert.That(_events[1], Does.EndWith(":TargetDisabled"));
            target.gameObject.SetActive(true);
            Sync();
            _rig.Sample();
            _rig.Filter(mask: 0);
            _rig.Sample();
            Assert.That(_events[3], Does.EndWith(":FilterChanged"));
        }

        [Test]
        public void CallbackFilterChangeCancelsOldDispatchAndNextCompleteUsesNewPolicy()
        {
            Target(Vector3.zero);
            Target(Vector3.right * 0.2f);
            int enters = 0;
            _rig.Entered += (_, __) =>
            {
                enters++;
                _rig.Filter(mask: 0);
            };
            Sync();
            _rig.Sample();
            Assert.That(enters, Is.EqualTo(1));
            _rig.Sample();
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_events.FindAll(e => e.EndsWith(":FilterChanged")).Count, Is.EqualTo(2));
        }

        [Test]
        public void CallbackStopAndReconfigureInvalidateTheOldCycle()
        {
            Target(Vector3.zero);
            Target(Vector3.right * 0.2f);
            int enters = 0;
            Action<Object, int> stop = (_, __) =>
            {
                enters++;
                _rig.Stop();
            };
            _rig.Entered += stop;
            Sync();
            _rig.Sample();
            Assert.That(enters, Is.EqualTo(1));
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_events.FindAll(e => e.EndsWith(":Stopped")).Count, Is.EqualTo(2));
            _rig.Entered -= stop;
            _rig.Start();
            _rig.Entered += (_, __) =>
            {
                enters++;
                _rig.Shapes(1, new[] { Vector3.right * 20 });
            };
            _rig.Sample();
            Assert.That(enters, Is.EqualTo(2));
            _rig.Sample();
            Assert.That(_rig.Count, Is.Zero);
            Assert.That(_events.FindAll(e => e.EndsWith(":Reconfigured")).Count, Is.EqualTo(2));
        }

        [Test]
        public void StayObserverFailureDoesNotKeepBorrowedBuffersOrDispatchFlags()
        {
            Target(Vector3.zero);
            Sync();
            _rig.Sample();
            Action<Object, int> fail = (_, __) => throw new InvalidOperationException("stay observer");
            _rig.Stayed += fail;
            Assert.Throws<InvalidOperationException>(() => _rig.Sample());
            Assert.That(_rig.IsSampling, Is.False);
            var buffer = (Array)_rig.Component.GetType().GetField("_buffer", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).GetValue(_rig.Component);
            foreach (var item in buffer)
            {
                Assert.That(item, Is.Null);
            }
            _rig.Stayed -= fail;
            Assert.That(_rig.Sample(), Is.True);
        }

        [Test]
        public void SharedConfigurationHasNoSceneReferencesAndInstancesHaveIndependentState()
        {
            var other = NewRig();
            other.Component.transform.position = Vector3.right * 20;
            _rig.ShareConfiguration(other);
            var first = Target(Vector3.zero);
            var second = Target(Vector3.right * 20);
            Sync();
            _rig.Sample();
            other.Sample();
            Assert.That(_rig.TargetAt(0), Is.SameAs(first));
            Assert.That(other.TargetAt(0), Is.SameAs(second));
            _rig.Stop();
            Assert.That(other.Count, Is.EqualTo(1));
            Assert.That(_rig.ConfigurationJson, Does.Not.Contain("instanceID"));
            other.Sample();
            Assert.That(other.TargetAt(0), Is.SameAs(second));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LocalShapesRespectTranslationRotationAndMirrors(int kind)
        {
            _rig.Component.transform.position = new Vector3(3, 7, 0);
            _rig.Component.transform.rotation = Quaternion.Euler(0, 0, 90);
            _rig.Component.transform.localScale = new Vector3(-2, 2, 2);
            _rig.Shapes(kind, new[] { Vector3.right });
            var target = Target(new Vector3(3, 5, 0));
            Target(new Vector3(3, 9, 0));
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_rig.Count, Is.EqualTo(1));
            Assert.That(_rig.TargetAt(0), Is.SameAs(target));
        }

        [Test]
        public void NonuniformRotatedBoxAndEllipticRoundShapesAreInvalidNotFakeExact()
        {
            Target(Vector3.zero);
            Sync();
            _rig.Sample();
            _rig.Component.transform.localScale = new Vector3(2, 1, 1);
            _rig.RotatedBox(37);
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(_rig.ConfigurationError, Is.Not.Empty);
            Assert.That(_rig.Count, Is.EqualTo(1));
            _rig.Shapes(1, new[] { Vector3.zero });
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.ConfigurationError, Is.Not.Empty);
        }

        [Test]
        public void BoxGeometryHasRotatedExtentsAndSupportsExactAxisAlignedNonuniformScale()
        {
            _rig.Shapes(0, new[] { Vector3.zero });
            _rig.Component.transform.rotation = Quaternion.Euler(0, 0, 90);
            _rig.Component.transform.localScale = new Vector3(-3, 1, 2);
            var inside = Target(Vector3.up * 0.9f);
            Target(Vector3.right * 0.9f);
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_rig.Count, Is.EqualTo(1));
            Assert.That(_rig.TargetAt(0), Is.SameAs(inside));
        }

        [Test]
        public void ParentShearIsDiagnosedInsteadOfUsingLossyScaleAsAnExactShape()
        {
            var parent = NewObject(Vector3.zero);
            parent.transform.localScale = new Vector3(2, 1, 1);
            _rig.Component.transform.SetParent(parent.transform, false);
            _rig.Component.transform.localRotation = Quaternion.Euler(0, 0, 37);
            _rig.Shapes(0, new[] { Vector3.zero });
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.ConfigurationError, Does.Contain("Sheared"));
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
        }

        [Test]
        public void SerializedGeometryAndCapacityErrorsKeepTheLastCompleteSnapshot()
        {
            Target(Vector3.zero);
            Sync();
            _rig.Sample();
            JsonUtility.FromJsonOverwrite("{\"shapes\":[{\"kind\":1,\"radius\":0}]}", _rig.Configuration);
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.ConfigurationError, Is.Not.Empty);
            Assert.That(_rig.Count, Is.EqualTo(1));
            Assert.That(_events.Count, Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _rig.Capacity(0, 2, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => _rig.Capacity(4, 2, true));
            _rig.Shapes(1, new[] { Vector3.zero });
            var field = _rig.Component.GetType().GetField("maxCapacity", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            field.SetValue(_rig.Component, 0);
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.ConfigurationError, Is.Not.Empty);
            Assert.That(_rig.Count, Is.EqualTo(1));
        }

        [TestCase(float.Epsilon)]
        [TestCase(float.MaxValue)]
        public void FinitePositiveFrequencyExtremesDoNotOverflowOrRejectManualSamples(float frequency)
        {
            Target(Vector3.zero);
            _rig.Frequency(frequency);
            Sync();
            Assert.That(_rig.Sample(), Is.True);
            Assert.That(_rig.Count, Is.EqualTo(1));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void IllegalSerializedFrequencyIsDiagnosedWithoutNativeCalls(float frequency)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _rig.Frequency(frequency));
            var field = _rig.Component.GetType().GetField("frequency", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            field.SetValue(_rig.Component, frequency);
            Assert.That(_rig.Sample(), Is.False);
            Assert.That(_rig.ConfigurationError, Is.Not.Empty);
            Assert.That(_rig.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
        }

        [UnityTest]
        public IEnumerator ManualUpdateAndFixedUpdateUseFinitePositiveScheduling()
        {
            Target(Vector3.zero);
            Sync();
            yield return null;
            yield return new WaitForFixedUpdate();
            Assert.That(_events, Is.Empty);
            _rig.Frequency(float.MaxValue);
            _rig.Mode(OverlapSamplingMode.Update);
            yield return null;
            yield return null;
            Assert.That(_rig.Count, Is.EqualTo(1));
            int before = _events.Count;
            _rig.Mode(OverlapSamplingMode.FixedUpdate);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.That(_events.Count, Is.GreaterThan(before));
            _rig.Stop();
            before = _events.Count;
            yield return null;
            yield return new WaitForFixedUpdate();
            Assert.That(_events.Count, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator OwnerSceneSwitchQueriesTheNewLocalPhysicsScene()
        {
            var first = Target(Vector3.zero);
            var secondScene = SceneManager.CreateScene("YL.Detector.Switch." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(_threeD ? LocalPhysicsMode.Physics3D : LocalPhysicsMode.Physics2D));
            try
            {
                var second = Target(Vector3.zero);
                SceneManager.MoveGameObjectToScene(second.gameObject, secondScene);
                Sync();
                _rig.Sample();
                Assert.That(_rig.TargetAt(0), Is.SameAs(first));
                SceneManager.MoveGameObjectToScene(_rig.Component.gameObject, secondScene);
                Assert.That(_events[1], Does.EndWith(":Disabled"), "Moving between local physics scenes disables/re-enables the native component.");
                _rig.Sample();
                Assert.That(_rig.TargetAt(0), Is.SameAs(second));
                yield return SceneManager.UnloadSceneAsync(secondScene);
                Assert.That(_events[_events.Count - 1], Does.EndWith(":Disabled"));
            }
            finally
            {
                if (secondScene.IsValid() && secondScene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(secondScene);
                }
            }
        }

        [UnityTest]
        public IEnumerator DefaultPhysicsSceneMoveLifecycleIsCharacterized()
        {
            var firstScene = SceneManager.CreateScene("YL.Detector.DefaultA." + Guid.NewGuid().ToString("N"));
            var secondScene = SceneManager.CreateScene("YL.Detector.DefaultB." + Guid.NewGuid().ToString("N"));
            try
            {
                var owner = NewObject(Vector3.up * 100);
                SceneManager.MoveGameObjectToScene(owner, firstScene);
                var rig = new Rig(owner, _threeD);
                var target = Target(Vector3.up * 100);
                SceneManager.MoveGameObjectToScene(target.gameObject, firstScene);
                int disables = 0;
                rig.Exited += (_, __, reason) =>
                {
                    if (reason == OverlapExitReason.Disabled)
                    {
                        disables++;
                    }
                };
                Sync();
                Assert.That(rig.Sample(), Is.True);
                Assert.That(rig.Count, Is.EqualTo(1));
                if (_threeD)
                {
                    Assert.That(firstScene.GetPhysicsScene(), Is.EqualTo(Physics.defaultPhysicsScene));
                    Assert.That(secondScene.GetPhysicsScene(), Is.EqualTo(Physics.defaultPhysicsScene));
                }
                else
                {
                    Assert.That(firstScene.GetPhysicsScene2D(), Is.EqualTo(Physics2D.defaultPhysicsScene));
                    Assert.That(secondScene.GetPhysicsScene2D(), Is.EqualTo(Physics2D.defaultPhysicsScene));
                }
                SceneManager.MoveGameObjectToScene(owner, secondScene);
                TestContext.WriteLine("Default physics scene move: threeD=" + _threeD + "; Disabled=" + disables +
                    "; LastComplete=" + rig.Count + "; IsRunning=" + rig.IsRunning);
                Assert.That(owner.scene, Is.EqualTo(secondScene));
                Assert.That(disables, Is.Zero);
                Assert.That(rig.Count, Is.EqualTo(1));
                Assert.That(rig.IsRunning, Is.True);
                yield return SceneManager.UnloadSceneAsync(secondScene);
                yield return SceneManager.UnloadSceneAsync(firstScene);
            }
            finally
            {
                if (secondScene.IsValid() && secondScene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(secondScene);
                }
                if (firstScene.IsValid() && firstScene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(firstScene);
                }
            }
        }

        [UnityTest]
        public IEnumerator NativeSceneChangedThenEnterStopKeepsBaselineEmptyAndRestartSafe() => SceneChangedThenEnterRelease(0);

        [UnityTest]
        public IEnumerator NativeSceneChangedThenEnterDisableKeepsBaselineEmptyAndRestartSafe() => SceneChangedThenEnterRelease(1);

        [UnityTest]
        public IEnumerator NativeSceneChangedThenEnterDestroyClearsTheDestroyedDetector() => SceneChangedThenEnterRelease(2);

        private IEnumerator SceneChangedThenEnterRelease(int action)
        {
            var firstScene = SceneManager.CreateScene("YL.Detector.ReleaseA." + Guid.NewGuid().ToString("N"));
            var secondScene = SceneManager.CreateScene("YL.Detector.ReleaseB." + Guid.NewGuid().ToString("N"));
            var firstPosition = Vector3.up * 150;
            var secondPosition = firstPosition + Vector3.right * 3;
            try
            {
                var owner = NewObject(firstPosition);
                SceneManager.MoveGameObjectToScene(owner, firstScene);
                var rig = new Rig(owner, _threeD);
                var first = Target(firstPosition);
                var second = Target(secondPosition);
                SceneManager.MoveGameObjectToScene(first.gameObject, firstScene);
                SceneManager.MoveGameObjectToScene(second.gameObject, secondScene);
                var two = rig.Component as OverlapDetector2D;
                var three = rig.Component as OverlapDetector3D;
                Func<int> completeCount = _threeD ? (Func<int>)(() => three.LastComplete.Count) : () => two.LastComplete.Count;
                Sync();
                Assert.That(rig.Sample(), Is.True);
                Assert.That(rig.TargetAt(0), Is.SameAs(first));
                var firstView = rig.Snapshot;
                int sceneExits = 0;
                int lifecycleExits = 0;
                int firstViewSeen = 0;
                int secondViewSeen = 0;
                int clearedInsideCallback = -1;
                var lifecycleEvents = new List<KeyValuePair<int, OverlapExitReason>>();
                rig.Exited += (_, id, reason) =>
                {
                    if (reason == OverlapExitReason.SceneChanged)
                    {
                        sceneExits++;
                        Assert.That(id, Is.EqualTo(first.GetInstanceID()));
                        Assert.That(rig.Count, Is.Zero);
                        foreach (var contact in firstView)
                        {
                            firstViewSeen++;
                        }
                    }
                    else
                    {
                        lifecycleExits++;
                        lifecycleEvents.Add(new KeyValuePair<int, OverlapExitReason>(id, reason));
                    }
                };
                Action<Object, int> releaseOnEnter = (_, id) =>
                {
                    Assert.That(id, Is.EqualTo(second.GetInstanceID()));
                    foreach (var contact in rig.Snapshot)
                    {
                        secondViewSeen++;
                        if (action == 0)
                        {
                            rig.Stop();
                        }
                        else if (action == 1)
                        {
                            rig.Component.enabled = false;
                        }
                        else
                        {
                            Object.DestroyImmediate(owner);
                        }
                        clearedInsideCallback = completeCount();
                    }
                };
                rig.Entered += releaseOnEnter;
                SceneManager.MoveGameObjectToScene(owner, secondScene);
                Assert.That(sceneExits, Is.Zero);
                Assert.That(lifecycleExits, Is.Zero, "Default-scene Move must not take the existing local-scene Disabled path.");
                Assert.That(rig.TargetAt(0), Is.SameAs(first));
                owner.transform.position = secondPosition;
                Sync();
                Assert.That(rig.Sample(), Is.False, "The Enter lifecycle callback must invalidate the current cycle.");
                int clearedCount = completeCount();
                rig.Entered -= releaseOnEnter;
                Exception restartError = null;
                bool restarted = false;
                int restartedCount = -1;
                try
                {
                    if (action == 2)
                    {
                        var replacement = NewObject(secondPosition);
                        SceneManager.MoveGameObjectToScene(replacement, secondScene);
                        var replacementRig = new Rig(replacement, _threeD);
                        restarted = replacementRig.Sample();
                        restartedCount = replacementRig.Count;
                    }
                    else
                    {
                        rig.Component.enabled = true;
                        rig.Start();
                        restarted = rig.Sample();
                        restartedCount = rig.Count;
                    }
                }
                catch (Exception error)
                {
                    restartError = error;
                }
                TestContext.WriteLine("Native double release: threeD=" + _threeD + "; action=" + action +
                    "; ClearedInsideCallback=" + clearedInsideCallback + "; ClearedCount=" + clearedCount + "; RestartError=" + restartError);
                Assert.That(clearedCount, Is.Zero, "The first retired scene baseline must not be resurrected. Restart error: " + restartError);
                Assert.That(clearedInsideCallback, Is.Zero);
                Assert.That(restartError, Is.Null);
                Assert.That(restarted, Is.True);
                Assert.That(restartedCount, Is.EqualTo(1));
                Assert.That(sceneExits, Is.EqualTo(1));
                Assert.That(lifecycleExits, Is.EqualTo(1));
                Assert.That(lifecycleEvents[0].Key, Is.EqualTo(second.GetInstanceID()));
                Assert.That(lifecycleEvents[0].Value, Is.EqualTo(action == 0 ? OverlapExitReason.Stopped : OverlapExitReason.Disabled));
                Assert.That(firstViewSeen, Is.EqualTo(1), "The first retired view stays valid inside its SceneChanged callback.");
                Assert.That(secondViewSeen, Is.EqualTo(1), "The second view stays valid inside its nested lifecycle callback.");
                yield return SceneManager.UnloadSceneAsync(secondScene);
                yield return SceneManager.UnloadSceneAsync(firstScene);
            }
            finally
            {
                if (secondScene.IsValid() && secondScene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(secondScene);
                }
                if (firstScene.IsValid() && firstScene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(firstScene);
                }
            }
        }

        private sealed class Rig
        {
            private readonly OverlapDetector2D _two;
            private readonly OverlapDetector3D _three;
            public readonly MonoBehaviour Component;
            public event Action<Object, int> Entered;
            public event Action<Object, int> Stayed;
            public event Action<Object, int, OverlapExitReason> Exited;

            public Rig(GameObject owner, bool threeD)
            {
                if (threeD)
                {
                    _three = owner.AddComponent<OverlapDetector3D>();
                    Component = _three;
                    _three.Entered += c => Entered?.Invoke(c.Target, c.Identity);
                    _three.Stayed += c => Stayed?.Invoke(c.Target, c.Identity);
                    _three.Exited += (c, r) => Exited?.Invoke(c.Target, c.Identity, r);
                }
                else
                {
                    _two = owner.AddComponent<OverlapDetector2D>();
                    Component = _two;
                    _two.Entered += c => Entered?.Invoke(c.Target, c.Identity);
                    _two.Stayed += c => Stayed?.Invoke(c.Target, c.Identity);
                    _two.Exited += (c, r) => Exited?.Invoke(c.Target, c.Identity, r);
                }
                Filter();
                Shapes(1, new[] { Vector3.zero });
            }

            public int Count => _three ? _three.LastComplete.Count : _two.LastComplete.Count;
            public bool IsRunning => _three ? _three.IsRunning : _two.IsRunning;
            public bool IsSampling => _three ? _three.IsSampling : _two.IsSampling;
            public OverlapQueryStatus Status => _three ? _three.LastQuery.Status : _two.LastQuery.Status;
            public int RawCount => _three ? _three.LastQuery.RawCount : _two.LastQuery.RawCount;
            public int BufferCapacity => _three ? _three.BufferCapacity : _two.BufferCapacity;
            public OverlapQueryInvalidationReason InvalidationReason => _three ? _three.LastQuery.InvalidationReason : _two.LastQuery.InvalidationReason;
            public string ConfigurationError => _three ? _three.ConfigurationError : _two.ConfigurationError;
            public object Configuration => _three ? (object)_three.Composite : _two.Composite;
            public IEnumerable Snapshot => _three ? (IEnumerable)_three.LastComplete : _two.LastComplete;
            public string ConfigurationJson => JsonUtility.ToJson(_three ? (object)_three.Composite : _two.Composite);
            public Object TargetAt(int index) => _three ? _three.LastComplete[index].Target : _two.LastComplete[index].Target;
            public bool Sample() => _three ? _three.Sample() : _two.Sample();
            public void Stop()
            {
                if (_three)
                {
                    _three.Stop();
                }
                else
                {
                    _two.Stop();
                }
            }
            public void Start()
            {
                if (_three)
                {
                    _three.StartDetection();
                }
                else
                {
                    _two.StartDetection();
                }
            }
            public void Capacity(int initial, int max, bool grow)
            {
                if (_three)
                {
                    _three.ConfigureCapacity(initial, max, grow);
                }
                else
                {
                    _two.ConfigureCapacity(initial, max, grow);
                }
            }
            public void Frequency(float value)
            {
                if (_three)
                {
                    _three.Frequency = value;
                }
                else
                {
                    _two.Frequency = value;
                }
            }
            public void Mode(OverlapSamplingMode value)
            {
                if (_three)
                {
                    _three.SamplingMode = value;
                }
                else
                {
                    _two.SamplingMode = value;
                }
            }
            public void Filter(bool triggers = false, bool groupByBody = false, int mask = 1)
            {
                if (_three)
                {
                    _three.Filter = new OverlapFilter3D(mask, triggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore,
                        groupByRigidbody: groupByBody);
                }
                else
                {
                    var contact = new ContactFilter2D { useTriggers = triggers };
                    contact.SetLayerMask(mask);
                    _two.Filter = new OverlapFilter2D(contact, groupByRigidbody: groupByBody);
                }
            }
            public void ShareConfiguration(Rig other)
            {
                if (_three)
                {
                    other._three.Composite = _three.Composite;
                }
                else
                {
                    other._two.Composite = _two.Composite;
                }
            }
            public void Shapes(int kind, Vector3[] centers)
            {
                if (_three)
                {
                    var shapes = new OverlapShape3D[centers.Length];
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        shapes[i] = kind == 0 ? OverlapShape3D.Box(centers[i], new Vector3(0.7f, 0.2f, 0.2f), Quaternion.identity) :
                            kind == 1 ? OverlapShape3D.Sphere(centers[i], 0.5f) :
                            OverlapShape3D.Capsule(centers[i] - Vector3.right * 0.2f, centers[i] + Vector3.right * 0.2f, 0.1f);
                    }
                    _three.Composite = new OverlapComposite3D(shapes);
                }
                else
                {
                    var shapes = new OverlapShape2D[centers.Length];
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        shapes[i] = kind == 0 ? OverlapShape2D.Box(centers[i], new Vector2(0.7f, 0.2f)) :
                            kind == 1 ? OverlapShape2D.Circle(centers[i], 0.5f) :
                            OverlapShape2D.Capsule(centers[i], new Vector2(0.6f, 0.2f), CapsuleDirection2D.Horizontal);
                    }
                    _two.Composite = new OverlapComposite2D(shapes);
                }
            }
            public void RotatedBox(float angle)
            {
                if (_three)
                {
                    _three.Composite = new OverlapComposite3D(OverlapShape3D.Box(Vector3.zero, Vector3.one,
                        Quaternion.Euler(0, 0, angle)));
                }
                else
                {
                    _two.Composite = new OverlapComposite2D(OverlapShape2D.Box(Vector2.zero, Vector2.one, angle));
                }
            }
        }
    }
}

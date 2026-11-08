using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Character2D;
using YLFramework.Platform2D;
using Object = UnityEngine.Object;

namespace YLFramework.Overlap.Tests
{
    public sealed class OverlapMovementTests
    {
        private GameObject _root;

        [SetUp]
        public void Setup() => _root = new GameObject("OwnedOverlapMovement");

        [TearDown]
        public void Cleanup() => Object.DestroyImmediate(_root);

        private GameObject Own(string name, Vector3 position, bool active = true)
        {
            var child = new GameObject(name);
            child.SetActive(active);
            child.transform.SetParent(_root.transform, false);
            child.transform.position = position;
            return child;
        }

        [UnityTest]
        public IEnumerator RealCharacterAndMovingPlatformRemainObservableByOverlapWithoutTakingOverMovement()
        {
            Assert.That(typeof(CharacterController2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Character2D"));
            Assert.That(typeof(MoveablePlatform2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Platform2D"));
            Assert.That(typeof(OverlapDetector2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Overlap"));
            var origin = new Vector3(1000, 1000, 0);
            var platformObject = Own("Platform", origin + Vector3.down * 0.5f, false);
            var platformCollider = platformObject.AddComponent<BoxCollider2D>();
            platformCollider.size = new Vector2(12, 1);
            var catcher = platformObject.AddComponent<PlatformCatcher2D>();
            catcher.areas = new[] { new Area { from = new Vector3(-5, 0.6f), to = new Vector3(5, 0.6f) } };
            var platform = platformObject.AddComponent<MoveablePlatform2D>();
            platform.Speed = 2;
            platform.PositionType = PositionType.Positions;
            platform.MoveType = MoveType.Once;
            platform.Positions.Add(platformObject.transform.position);
            platform.Positions.Add(platformObject.transform.position + Vector3.right * 2);
            platformObject.SetActive(true);
            platform.Stop();
            var character = Own("Character", origin + Vector3.up * 2, false);
            var body = character.AddComponent<Rigidbody2D>();
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            character.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
            var motor = character.AddComponent<CharacterController2D>();
            motor.onGroundValueChange = new YLFramework.Character2D.BoolUnityEvent();
            motor.onVelocityValueChange = new Vector2UnityEvent();
            motor.onJumpCountValueChange = new IntUnityEvent();
            var detector = character.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Circle(Vector2.zero, 3));
            detector.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 }, ignoredRoot: character.transform);
            character.SetActive(true);
            for (int i = 0; i < 100 && !motor.IsGrounded; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded, Is.True, "The real product motor must land on the real platform collider.");
            Assert.That(motor.GroundInfo.collider, Is.SameAs(platformCollider));
            Physics2D.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.LastComplete.Select(contact => contact.Collider), Does.Contain(platformCollider));
            catcher.RefreshContacts();
            Assert.That(catcher.Contacts.Any(hit => hit.rigidbody == body), Is.True);
            Vector2 before = body.position;
            float platformBefore = platform.transform.position.x;
            platform.Run();
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            platform.Pause(true);
            Assert.That(platform.transform.position.x - platformBefore, Is.GreaterThan(0.2f));
            Assert.That(body.position.x - before.x, Is.GreaterThan(0.2f), "Platform2D, not the test or detector, must carry its native passenger.");
            Physics2D.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.LastComplete.Select(contact => contact.Collider), Does.Contain(platformCollider));
            var hits = new RaycastHit2D[8];
            Assert.That(OverlapQuery2D.Cast(OverlapShape2D.Point(character.transform.position), Vector2.down, 4,
                detector.Filter, hits).Count, Is.EqualTo(1));
            Assert.That(hits[0].collider, Is.SameAs(platformCollider));
            detector.Stop();
            Assert.That(detector.LastComplete, Is.Empty);
            Assert.That(motor.enabled && platform.enabled, Is.True, "Stopping a detector cannot stop a movement product.");
        }

        [Test]
        public void BoxSphereUnionKeepsRealContactIdentityInTheMovementInstallation()
        {
            var target = Own("Native3DTarget", new Vector3(50, 50, 7)).AddComponent<BoxCollider>();
            target.gameObject.layer = 11;
            var host = Own("Union", new Vector3(50, 50, 7));
            var detector = host.AddComponent<OverlapDetector3D>();
            detector.Filter = new OverlapFilter3D(1 << 11);
            detector.Composite = new OverlapComposite3D(OverlapShape3D.Box(Vector3.zero, Vector3.one * 3, Quaternion.identity),
                OverlapShape3D.Sphere(Vector3.zero, 2));
            Physics.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.LastComplete.Single().Collider, Is.SameAs(target));
            Assert.That(detector.LastComplete.Single().Point.z, Is.EqualTo(7));
            detector.enabled = false;
            Assert.That(detector.LastComplete, Is.Empty);
        }
    }
}

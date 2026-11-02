using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Character2D;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class CharacterMotor2DTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        private GameObject NewObject(string name, Vector3 position)
        {
            var obj = new GameObject("YL.Behavior." + name);
            obj.transform.position = position;
            _owned.Add(obj);
            return obj;
        }

        private CharacterController2D Motor(Vector3 center)
        {
            var obj = NewObject("Motor", center);
            obj.SetActive(false);
            obj.AddComponent<Rigidbody2D>().gravityScale = 2;
            obj.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
            var motor = obj.AddComponent<CharacterController2D>();
            motor.maxJumpCount = 2;
            motor.onJumpCountValueChange = new IntUnityEvent();
            obj.SetActive(true);
            return motor;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var obj in _owned)
            {
                if (obj)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            _owned.Clear();
        }

        [Test]
        public void JumpCountIsCommittedBeforeCallbackAndLimitIsPreserved()
        {
            var motor = Motor(new Vector3(14000, 14000));
            int observed = 0;
            motor.onJumpCountValueChange.AddListener(value =>
            {
                Assert.That(motor.CurrentJumpCount, Is.EqualTo(value));
                observed++;
            });
            motor.Jump(5);
            motor.Jump(5);
            motor.Jump(5);
            Assert.That(motor.CurrentJumpCount, Is.EqualTo(2));
            Assert.That(observed, Is.EqualTo(2));
        }

        [Test]
        public void DisableRestoresGravityAndDiscardsQueuedMovementAndJump()
        {
            var motor = Motor(new Vector3(14000, 14100));
            motor.Move(new Vector2(2, 0));
            motor.JumpStart(5, 10);
            motor.enabled = false;
            Assert.That(motor.Rigidbody.gravityScale, Is.EqualTo(2));
            Assert.That(motor.CurrentJumpCount, Is.Zero);
            Assert.That(motor.Velocity, Is.EqualTo(Vector2.zero));
            motor.enabled = true;
            Assert.That(motor.IsGrounded || motor.IsTouchingWall || motor.IsTouchingCeiling, Is.False);
            Assert.That(typeof(CharacterController2D).GetField("_moveDelta", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motor), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void TwoMotorsHaveDistinctRaycastAndContactBuffers()
        {
            var a = Motor(new Vector3(14000, 14200));
            var b = Motor(new Vector3(14010, 14200));
            foreach (var name in new[] { "Hits", "Contacts" })
            {
                var field = typeof(CharacterController2D).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null);
                Assert.That(field.GetValue(a), Is.Not.SameAs(field.GetValue(b)));
            }
        }

        [Test]
        public void MissingColliderIsDiagnosedAndExplicitRefreshRecovers()
        {
            var obj = NewObject("InvalidMotor", new Vector3(14000, 14300));
            obj.SetActive(false);
            var motor = obj.AddComponent<CharacterController2D>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Rigidbody2D requires an attached"));
            obj.SetActive(true);
            obj.AddComponent<CircleCollider2D>();
            Assert.That(motor.RefreshColliders(), Is.True);
        }

        [Test]
        public void FailedColliderRefreshReturnsGravityAndDropsOldInputBeforeRecovery()
        {
            var motor = Motor(new Vector3(14000, 14500));
            var fixedUpdate = typeof(CharacterController2D).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            fixedUpdate.Invoke(motor, null);
            Assert.That(motor.Rigidbody.gravityScale, Is.Zero);
            motor.Move(Vector2.right);
            var collider = motor.GetComponent<BoxCollider2D>();
            collider.isTrigger = true;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Rigidbody2D requires an attached"));
            Assert.That(motor.RefreshColliders(), Is.False);
            Assert.That(motor.Rigidbody.gravityScale, Is.EqualTo(2));
            Assert.That(typeof(CharacterController2D).GetField("_moveDelta", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motor), Is.EqualTo(Vector2.zero));
            Assert.That(motor.IsGrounded || motor.IsTouchingCeiling || motor.IsTouchingWall, Is.False);
            collider.isTrigger = false;
            Assert.That(motor.RefreshColliders(), Is.True);
            fixedUpdate.Invoke(motor, null);
            Assert.That(motor.Rigidbody.gravityScale, Is.Zero);
        }

        [Test]
        public void DisableFromRightWallCallbackDoesNotContinueIntoLeftWallQuery()
        {
            var center = new Vector3(14000, 14600);
            var motor = Motor(center);
            motor.ChecksWalls = true;
            motor.CheckLayerMask = 1;
            foreach (float sign in new[] { -1f, 1f })
            {
                NewObject("Wall", center + Vector3.right * sign * 0.75f).AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 6);
            }

            int notifications = 0;
            motor.onTouchWallValueChange = new BoolUnityEvent();
            motor.onTouchWallValueChange.AddListener(value =>
            {
                if (value)
                {
                    notifications++;
                    motor.enabled = false;
                }
            });
            var wall = new CheckWall();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(CheckWall).GetField("_character", flags).SetValue(wall, motor);
            typeof(CheckWall).GetField("_wallAngle", flags).SetValue(wall, 60f);
            typeof(CheckWall).GetField("_lastCheckTime", flags).SetValue(wall, float.NegativeInfinity);
            Physics2D.SyncTransforms();
            typeof(CheckWall).GetMethod("CheckWallTouch", flags).Invoke(wall, null);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(motor.IsTouchingWall, Is.False);
            Assert.That(motor.WallInfo.collider, Is.Null);
        }

        [TestCase(0f, false)]
        [TestCase(30f, false)]
        [TestCase(90f, false)]
        [TestCase(0f, true)]
        [TestCase(30f, true)]
        [TestCase(90f, true)]
        public void RotatedBoxAndCapsuleCastTheActualShapeRatherThanItsRotatedAabb(float angle, bool capsule)
        {
            var center = new Vector3(14000, 14700);
            var obj = NewObject("RotatedMotor", center);
            obj.SetActive(false);
            var body = obj.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            Collider2D shape;
            if (capsule)
            {
                var c = obj.AddComponent<CapsuleCollider2D>();
                c.size = new Vector2(1, 2);
                shape = c;
            }
            else
            {
                var c = obj.AddComponent<BoxCollider2D>();
                c.size = new Vector2(1, 2);
                shape = c;
            }
            var motor = obj.AddComponent<CharacterController2D>();
            obj.SetActive(true);
            motor.CheckLayerMask = 1;
            body.rotation = angle;
            Physics2D.SyncTransforms();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(CharacterController2D.GetColliderCastAngle(shape), angle)), Is.LessThan(0.001f));
            Vector2 surface = shape.ClosestPoint((Vector2)center + Vector2.up * 100);
            var obstacle = NewObject("QueryObstacle", surface + Vector2.up * 0.04f).AddComponent<BoxCollider2D>();
            obstacle.size = Vector2.one * 0.02f;
            Physics2D.SyncTransforms();
            int count = capsule ? motor.Raycast((CapsuleCollider2D)shape, Vector2.up) : motor.Raycast((BoxCollider2D)shape, Vector2.up);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                found |= motor.Hits[i].collider == obstacle;
            }

            string pose = $"requested={angle}; body={body.rotation}; transform={shape.transform.eulerAngles.z}; bounds={shape.bounds.size}; size={(capsule ? ((CapsuleCollider2D)shape).size : ((BoxCollider2D)shape).size)}; surface={surface}";
            Assert.That(found, Is.True, "near obstacle is within the original 0.1 query distance; " + pose);
            obstacle.transform.position = surface + Vector2.up * 0.25f;
            Physics2D.SyncTransforms();
            count = capsule ? motor.Raycast((CapsuleCollider2D)shape, Vector2.up) : motor.Raycast((BoxCollider2D)shape, Vector2.up);
            for (int i = 0; i < count; i++)
            {
                Assert.That(motor.Hits[i].collider, Is.Not.SameAs(obstacle), "outside the true shape and query distance; " + pose + $"; realDistance={Vector2.Distance(shape.ClosestPoint(obstacle.transform.position), obstacle.transform.position)}");
            }
        }

        [UnityTest]
        public IEnumerator GroundAndMultipleAttachedCollidersSurviveDisableEnable()
        {
            var center = new Vector3(14000, 14400);
            var floor = NewObject("Floor", center + Vector3.down * 1.5f).AddComponent<BoxCollider2D>();
            floor.size = new Vector2(10, 1);
            var motor = Motor(center);
            var child = NewObject("SecondCollider", center + Vector3.up * 0.5f);
            child.transform.SetParent(motor.transform, true);
            child.AddComponent<CircleCollider2D>().radius = 0.2f;
            Assert.That(motor.RefreshColliders(), Is.True);
            Physics2D.SyncTransforms();
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded, Is.True);
            Assert.That(motor.GroundInfo.collider, Is.SameAs(floor));
            motor.enabled = false;
            motor.enabled = true;
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded, Is.True);
        }

        [TestCase(90f, 30f)]
        [TestCase(180f, -30f)]
        public void ChildColliderKeepsItsLocalAngleWhenBodyPosePrecedesTransform(float bodyAngle, float childAngle)
        {
            var center = new Vector3(14000, 14800);
            var obj = NewObject("Body", center);
            obj.SetActive(false);
            var body = obj.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            var child = NewObject("ChildShape", center);
            child.transform.SetParent(obj.transform, true);
            child.transform.localRotation = Quaternion.Euler(0, 0, childAngle);
            var shape = child.AddComponent<BoxCollider2D>();
            obj.AddComponent<CharacterController2D>();
            obj.SetActive(true);
            body.rotation = bodyAngle;
            Physics2D.SyncTransforms();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(CharacterController2D.GetColliderCastAngle(shape), bodyAngle + childAngle)), Is.LessThan(0.001f));
        }
    }
}

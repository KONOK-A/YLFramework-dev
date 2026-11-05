using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Character2D;
using YLFramework.Platform2D;

namespace YLFramework.Tests.Movement2D
{
    [Category("YL.Module.Movement2D")]
    public sealed class Movement2DPhysicsBoundaryTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        private float _timeScale;
        private float _fixedDeltaTime;
        private SimulationMode2D _simulationMode;
        private Vector2 _gravity;

        [SetUp]
        public void ConfigureClock()
        {
            _timeScale = Time.timeScale;
            _fixedDeltaTime = Time.fixedDeltaTime;
            _simulationMode = Physics2D.simulationMode;
            _gravity = Physics2D.gravity;
            Time.timeScale = 1;
            Time.fixedDeltaTime = 0.02f;
            Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
            Physics2D.gravity = new Vector2(0, -9.81f);
            Assert.That(Physics2D.GetIgnoreLayerCollision(0, 0), Is.False);
        }

        private GameObject Own(string name, Vector3 position, bool active = true)
        {
            var obj = new GameObject("YL.Movement2D." + name);
            obj.SetActive(active);
            obj.transform.position = position;
            _owned.Add(obj);
            return obj;
        }

        private CharacterController2D Motor(Vector3 origin)
        {
            var obj = Own("Motor", origin, false);
            var body = obj.AddComponent<Rigidbody2D>();
            body.gravityScale = 2;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            obj.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
            var motor = obj.AddComponent<CharacterController2D>();
            motor.gravityScale = 2;
            motor.maxJumpCount = 2;
            motor.ChecksWalls = true;
            motor.ChecksCeiling = true;
            motor.onGroundValueChange = new Character2D.BoolUnityEvent();
            motor.onTouchWallValueChange = new Character2D.BoolUnityEvent();
            motor.onTopValueChange = new Character2D.BoolUnityEvent();
            motor.onJumpCountValueChange = new IntUnityEvent();
            motor.onVelocityValueChange = new Vector2UnityEvent();
            obj.SetActive(true);
            return motor;
        }

        private Rigidbody2D Passenger(Vector3 position)
        {
            var obj = Own("Passenger", position);
            var body = obj.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            obj.AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 0.8f);
            obj.AddComponent<CircleCollider2D>().radius = 0.2f;
            return body;
        }

        [TearDown]
        public void Cleanup()
        {
            try
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
            finally
            {
                Time.timeScale = _timeScale;
                Time.fixedDeltaTime = _fixedDeltaTime;
                Physics2D.simulationMode = _simulationMode;
                Physics2D.gravity = _gravity;
            }
        }

        [UnityTest]
        public IEnumerator RealGroundWallJumpAndCeilingPublishCommittedPublicStateAndRespectPausedPhysics()
        {
            var origin = new Vector3(18000, 18000);
            var floor = Own("Floor", origin + Vector3.down * 1.5f).AddComponent<BoxCollider2D>();
            floor.size = new Vector2(12, 1);
            var wall = Own("Wall", origin + new Vector3(0.95f, 1)).AddComponent<BoxCollider2D>();
            wall.size = new Vector2(0.5f, 6);
            var ceiling = Own("Ceiling", origin + Vector3.up * 1.85f).AddComponent<BoxCollider2D>();
            ceiling.size = new Vector2(4, 0.5f);
            var motor = Motor(origin);
            int grounds = 0, walls = 0, tops = 0, positiveJumps = 0, landings = 0, velocities = 0;
            motor.onGroundValueChange.AddListener(value =>
            {
                Assert.That(motor.IsGrounded, Is.EqualTo(value));
                if (value)
                {
                    grounds++;
                    Assert.That(motor.GroundInfo.collider, Is.SameAs(floor));
                }
            });
            motor.onTouchWallValueChange.AddListener(value =>
            {
                Assert.That(motor.IsTouchingWall, Is.EqualTo(value));
                if (value)
                {
                    walls++;
                    Assert.That(motor.WallInfo.collider, Is.SameAs(wall));
                }
            });
            motor.onTopValueChange.AddListener(value =>
            {
                Assert.That(motor.IsTouchingCeiling, Is.EqualTo(value));
                if (value)
                {
                    tops++;
                    Assert.That(motor.TopInfo.collider, Is.SameAs(ceiling));
                }
            });
            motor.onJumpCountValueChange.AddListener(value =>
            {
                Assert.That(motor.CurrentJumpCount, Is.EqualTo(value));
                if (value > 0)
                {
                    positiveJumps++;
                }
                else
                {
                    landings++;
                }
            });
            motor.onVelocityValueChange.AddListener(value =>
            {
                Assert.That(motor.Velocity, Is.EqualTo(value));
                if (value.sqrMagnitude > 0.01f)
                {
                    velocities++;
                }
            });
            Physics2D.SyncTransforms();
            for (int step = 0; step < 80 && !motor.IsGrounded; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded && grounds > 0, Is.True);
            Assert.That(Vector2.Dot(motor.GroundInfo.normal, Vector2.up), Is.GreaterThan(0.9f));
            for (int step = 0; step < 80 && !motor.IsTouchingWall; step++)
            {
                motor.MoveHorizontal(2);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsTouchingWall && walls > 0, Is.True);
            Assert.That(motor.WallInfo.normal.x, Is.LessThan(-0.9f));
            float startY = motor.Rigidbody.position.y;
            motor.Jump(6);
            yield return new WaitForFixedUpdate();
            yield return null;
            var pausedAt = motor.Rigidbody.position;
            float pausedClock = Time.fixedTime;
            Time.timeScale = 0;
            yield return new WaitForSecondsRealtime(0.08f);
            Assert.That(Vector2.Distance(motor.Rigidbody.position, pausedAt), Is.LessThan(0.001f));
            Assert.That(Time.fixedTime, Is.EqualTo(pausedClock));
            Time.timeScale = 1;
            float peak = pausedAt.y;
            for (int step = 0; step < 100 && tops == 0; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
                peak = Mathf.Max(peak, motor.Rigidbody.position.y);
            }
            Assert.That(tops, Is.GreaterThan(0), "A real upward jump must produce the ceiling callback, not a reflected setter.");
            Assert.That(peak - startY, Is.GreaterThan(0.3f));
            Assert.That(Vector2.Dot(motor.TopInfo.normal, Vector2.down), Is.GreaterThan(0.9f));
            for (int step = 0; step < 100 && (!motor.IsGrounded || motor.CurrentJumpCount != 0); step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded && motor.CurrentJumpCount == 0, Is.True);
            Assert.That(grounds, Is.GreaterThanOrEqualTo(2));
            Assert.That(positiveJumps, Is.EqualTo(1));
            Assert.That(landings, Is.EqualTo(1));
            Assert.That(velocities, Is.GreaterThan(0), "Do not require an instantaneous first-step velocity; require observed movement callbacks.");
        }

        [UnityTest]
        public IEnumerator JumpCallbackDisableCancelsTheOldFlowAndPooledMotorAcceptsOnlyFreshInput()
        {
            var origin = new Vector3(18000, 18100);
            Own("PoolFloor", origin + Vector3.down * 1.5f).AddComponent<BoxCollider2D>().size = new Vector2(12, 1);
            var motor = Motor(origin);
            bool disable = true;
            int positiveJumps = 0;
            motor.onJumpCountValueChange.AddListener(value =>
            {
                Assert.That(motor.CurrentJumpCount, Is.EqualTo(value));
                if (value > 0)
                {
                    positiveJumps++;
                    if (disable)
                    {
                        motor.enabled = false;
                    }
                }
            });
            motor.Move(Vector2.right * 4);
            motor.JumpStart(6, 12);
            Assert.That(motor.enabled, Is.False);
            Assert.That(motor.CurrentJumpCount, Is.Zero);
            Assert.That(motor.Velocity, Is.EqualTo(Vector2.zero));
            Assert.That(motor.Rigidbody.gravityScale, Is.EqualTo(2));
            motor.gameObject.SetActive(false);
            motor.Move(Vector2.right * 10);
            motor.Jump(8);
            var reusedAt = (Vector2)origin + Vector2.up * 3;
            motor.transform.position = reusedAt;
            motor.Rigidbody.position = reusedAt;
#if UNITY_6000_0_OR_NEWER
            motor.Rigidbody.linearVelocity = Vector2.zero;
#else
            motor.Rigidbody.velocity = Vector2.zero;
#endif
            motor.gravityScale = 0;
            disable = false;
            motor.enabled = true;
            motor.gameObject.SetActive(true);
            Assert.That(Vector2.Distance(reusedAt, motor.Rigidbody.position), Is.LessThan(0.01f),
                $"The pooled test setup must actually teleport before observing movement: requested={reusedAt}, body={motor.Rigidbody.position}, transform={motor.transform.position}.");
            Assert.That(motor.CurrentJumpCount, Is.Zero);
            Assert.That(motor.IsGrounded || motor.IsTouchingWall || motor.IsTouchingCeiling, Is.False);
            Assert.That(motor.GroundInfo.collider || motor.WallInfo.collider || motor.TopInfo.collider, Is.False);
            Physics2D.SyncTransforms();
            for (int step = 0; step < 8; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(Vector2.Distance(reusedAt, motor.Rigidbody.position), Is.LessThan(0.01f), "No previous queued delta, upward speed or jump-abort state may survive reuse.");
            motor.gravityScale = 2;
            for (int step = 0; step < 100 && !motor.IsGrounded; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.IsGrounded, Is.True);
            float startY = motor.Rigidbody.position.y;
            motor.Jump(5);
            for (int step = 0; step < 6; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(motor.Rigidbody.position.y - startY, Is.GreaterThan(0.2f));
            Assert.That(motor.CurrentJumpCount, Is.EqualTo(1));
            Assert.That(positiveJumps, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator DisabledPathHoldsProgressAndPooledCatcherCarriesOnlyTheReplacementPassenger()
        {
            var origin = new Vector3(18000, 18200);
            var obj = Own("ReusablePlatform", origin, false);
            obj.AddComponent<BoxCollider2D>().size = new Vector2(4, 1);
            var platform = obj.AddComponent<MoveablePlatform2D>();
            platform.Positions.Add(origin);
            platform.Positions.Add(origin + Vector3.right * 4);
            platform.MoveType = MoveType.Once;
            platform.Speed = 4;
            var catcher = obj.GetComponent<PlatformCatcher2D>();
            catcher.areas = new[]
            {
                new Area { from = new Vector3(-1.5f, 1), to = new Vector3(1.5f, 1) },
                new Area { from = new Vector3(-1.5f, 1), to = new Vector3(1.5f, 1) }
            };
            var oldPassenger = Passenger(origin + Vector3.up);
            obj.SetActive(true);
            Physics2D.SyncTransforms();
            catcher.RefreshContacts();
            Assert.That(catcher.Contacts.Exists(hit => hit.rigidbody == oldPassenger), Is.True);
            for (int step = 0; step < 3; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            platform.enabled = false;
            var held = platform.transform.position;
            Assert.That(platform.IsRunning, Is.True, "Disable holds the path's existing state; it is not an implicit Stop.");
            for (int step = 0; step < 3; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(platform.transform.position, Is.EqualTo(held));
            platform.enabled = true;
            for (int step = 0; step < 3; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(platform.transform.position.x, Is.GreaterThan(held.x));
            platform.Stop();
            obj.SetActive(false);
            Assert.That(catcher.Contacts, Is.Empty);
            oldPassenger.position += Vector2.up * 10;
            var oldPosition = oldPassenger.position;
            var platformStart = platform.transform.position;
            var replacement = Passenger(platformStart + Vector3.up);
            var passengerStart = replacement.position;
            obj.SetActive(true);
            Physics2D.SyncTransforms();
            catcher.RefreshContacts();
            Assert.That(catcher.Contacts.Exists(hit => hit.rigidbody == replacement), Is.True);
            Assert.That(catcher.Contacts.Exists(hit => hit.rigidbody == oldPassenger), Is.False);
            platform.Run();
            for (int step = 0; step < 10; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.That(Vector2.Distance(oldPosition, oldPassenger.position), Is.LessThan(0.001f));
            var delta = (Vector2)(platform.transform.position - platformStart);
            Assert.That(delta.magnitude, Is.GreaterThan(0.1f));
            Assert.That(Vector2.Distance(replacement.position - passengerStart, delta), Is.LessThan(0.03f));
        }
    }
}

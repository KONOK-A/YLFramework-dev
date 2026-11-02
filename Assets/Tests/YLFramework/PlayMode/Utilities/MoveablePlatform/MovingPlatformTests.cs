using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using YLFramework.Platform2D;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class MovingPlatformTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        private float _originalTimeScale;
        private float _originalFixedDeltaTime;

        [SetUp]
        public void ConfigureFixedClock()
        {
            _originalTimeScale = Time.timeScale;
            _originalFixedDeltaTime = Time.fixedDeltaTime;
            Time.timeScale = 1;
            Time.fixedDeltaTime = 0.02f;
        }

        private GameObject NewObject(string name, Vector3 position)
        {
            var obj = new GameObject("YL.Behavior." + name);
            obj.transform.position = position;
            _owned.Add(obj);
            return obj;
        }

        private Moveable NewMoveable(Vector3 origin, params Vector3[] points)
        {
            var obj = NewObject("Moveable", origin);
            obj.SetActive(false);
            var moveable = obj.AddComponent<Moveable>();
            moveable.Positions.AddRange(points);
            obj.SetActive(true);
            return moveable;
        }

        private MoveablePlatform2D NewInactivePlatform(Vector3 origin, MoveType moveType, float speed, float delay = 0)
        {
            var obj = NewObject("FixedPlatform", origin);
            obj.SetActive(false);
            var platform = obj.AddComponent<MoveablePlatform2D>();
            platform.Positions.Add(origin);
            platform.Positions.Add(origin + Vector3.right * 2);
            platform.MoveType = moveType;
            platform.Speed = speed;
            platform.Delay = delay;
            return platform;
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
                Time.timeScale = _originalTimeScale;
                Time.fixedDeltaTime = _originalFixedDeltaTime;
            }
        }

        [UnityTest]
        public IEnumerator SingleNodeCompletesSafelyAndPauseStopDoNotRestartStoppedPath()
        {
            var point = new Vector3(16000, 16000);
            var moveable = NewMoveable(point + Vector3.right, point);
            yield return new WaitForFixedUpdate();
            Assert.That(moveable.transform.position, Is.EqualTo(point));
            Assert.That(moveable.IsRunning, Is.False);
            moveable.Pause(false);
            Assert.That(moveable.IsRunning, Is.False);
        }

        [UnityTest]
        public IEnumerator StopFromMoveCallbackPreventsPositionCommit()
        {
            var point = new Vector3(16000, 16100);
            var moveable = NewMoveable(point, point, point + Vector3.right * 4);
            moveable.onMove = new Vector3UnityEvent();
            moveable.onMove.AddListener(_ => moveable.Stop());
            yield return new WaitForFixedUpdate();
            Assert.That(moveable.transform.position, Is.EqualTo(point));
            Assert.That(moveable.IsRunning, Is.False);
        }

        [Test]
        public void DeletedTransformGivesIndexedDiagnosticWithoutBreakingAnotherPath()
        {
            var point = new Vector3(16000, 16200);
            var moveable = NewMoveable(point, point, point + Vector3.right);
            moveable.Stop();
            moveable.PositionType = PositionType.Transforms;
            var node = NewObject("Node", point);
            moveable.Transforms.Add(node.transform);
            moveable.Transforms.Add(null);
            Assert.That(moveable.ValidatePath(out string error), Is.False);
            Assert.That(error, Does.Contain("1"));
            var other = NewMoveable(point + Vector3.up * 10, point, point + Vector3.right);
            Assert.That(other.ValidatePath(out _), Is.True);
        }

        [Test]
        public void CarrierMovesEachRigidbodyOnlyOnceAcrossAreasAndMultipleColliders()
        {
            var point = new Vector3(16000, 16300);
            var platformObject = NewObject("Carrier", point);
            platformObject.SetActive(false);
            var catcher = platformObject.AddComponent<PlatformCatcher2D>();
            var platform = platformObject.AddComponent<MoveablePlatform2D>();
            platform.Positions.Add(point);
            platform.Positions.Add(point + Vector3.right * 4);
            catcher.areas = new[] { new Area { from = new Vector3(-2, 1), to = new Vector3(2, 1) }, new Area { from = new Vector3(-2, 1), to = new Vector3(2, 1) } };
            var passenger = NewObject("Passenger", point + Vector3.up);
            var body = passenger.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            passenger.AddComponent<BoxCollider2D>();
            passenger.AddComponent<CircleCollider2D>();
            platformObject.SetActive(true);
            Physics2D.SyncTransforms();
            catcher.RefreshContacts();
            Assert.That(catcher.Contacts.Count, Is.GreaterThanOrEqualTo(2));
            var before = body.position;
            typeof(MoveablePlatform2D).GetMethod("Move", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null).Invoke(platform, new object[] { point + Vector3.right });
            Assert.That(body.position, Is.EqualTo(before + Vector2.right));
            catcher.enabled = false;
            Assert.That(catcher.Contacts, Is.Empty);
        }

        [UnityTest]
        public IEnumerator LoopWrapsAfterTraversingThePathDuringRealFixedUpdates()
        {
            var origin = new Vector3(16000, 16400);
            var platform = NewInactivePlatform(origin, MoveType.Loop, 10);
            float previous = 0, furthest = 0;
            bool wrapped = false, onlyFixedUpdates = true;
            platform.onMove = new Vector3UnityEvent();
            platform.onMove.AddListener(_ =>
            {
                // onMove 在提交前触发，读取的是上一次 FixedUpdate 已提交的位置。
                float committed = platform.transform.position.x - origin.x;
                onlyFixedUpdates &= Time.inFixedTimeStep;
                if (previous > 1.5f && committed < 0.5f)
                {
                    wrapped = true;
                }

                furthest = Mathf.Max(furthest, committed);
                previous = committed;
            });
            platform.gameObject.SetActive(true);
            for (int i = 0; i < 24 && !wrapped; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(furthest, Is.GreaterThan(1.5f));
            Assert.That(wrapped, Is.True, "Loop must wrap to the start, not stop or reverse.");
            Assert.That(onlyFixedUpdates, Is.True);
            Assert.That(platform.IsRunning, Is.True);
        }

        [UnityTest]
        public IEnumerator PingPongReturnsFromTheEndWithoutALoopJumpDuringRealFixedUpdates()
        {
            var origin = new Vector3(16000, 16500);
            var platform = NewInactivePlatform(origin, MoveType.PingPong, 10);
            var committedPositions = new List<float>();
            bool onlyFixedUpdates = true;
            platform.onMove = new Vector3UnityEvent();
            platform.onMove.AddListener(_ =>
            {
                committedPositions.Add(platform.transform.position.x - origin.x);
                onlyFixedUpdates &= Time.inFixedTimeStep;
            });
            platform.gameObject.SetActive(true);
            for (int i = 0; i < 22; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            int turnaround = committedPositions.FindIndex(x => x >= 1.8f);
            Assert.That(turnaround, Is.GreaterThanOrEqualTo(0));
            Assert.That(committedPositions.FindIndex(turnaround + 1, x => x <= 0.4f), Is.GreaterThan(turnaround));
            for (int i = 1; i < committedPositions.Count; i++)
            {
                Assert.That(committedPositions[i], Is.InRange(0f, 2f));
                Assert.That(Mathf.Abs(committedPositions[i] - committedPositions[i - 1]), Is.LessThanOrEqualTo(0.21f),
                    "PingPong must reverse continuously rather than jump to the start.");
            }
            Assert.That(onlyFixedUpdates, Is.True);
            Assert.That(platform.IsRunning, Is.True);
        }

        [UnityTest]
        public IEnumerator OnceCommitsEndpointBeforeItsSingleStopEvent()
        {
            var origin = new Vector3(16000, 16600);
            var platform = NewInactivePlatform(origin, MoveType.Once, 10);
            int starts = 0, stops = 0;
            var positionAtStop = origin;
            platform.onStartRun = new UnityEvent();
            platform.onStop = new UnityEvent();
            platform.onStartRun.AddListener(() => starts++);
            platform.onStop.AddListener(() =>
            {
                stops++;
                positionAtStop = platform.transform.position;
            });
            platform.gameObject.SetActive(true);
            for (int i = 0; i < 16 && platform.IsRunning; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(platform.IsRunning, Is.False);
            Assert.That(platform.transform.position, Is.EqualTo(origin + Vector3.right * 2));
            Assert.That(positionAtStop, Is.EqualTo(origin + Vector3.right * 2));
            Assert.That(starts, Is.EqualTo(1));
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(stops, Is.EqualTo(1));
            Assert.That(platform.transform.position, Is.EqualTo(positionAtStop));
        }

        [UnityTest]
        public IEnumerator DelayDefersFirstCommittedMovementUsingTheActualFixedClock()
        {
            var origin = new Vector3(16000, 16700);
            var platform = NewInactivePlatform(origin, MoveType.Once, 10, 0.12f);
            float startedAt = Time.fixedTime, firstMoveAt = -1;
            platform.onMove = new Vector3UnityEvent();
            platform.onMove.AddListener(_ =>
            {
                if (firstMoveAt < 0)
                {
                    firstMoveAt = Time.fixedTime;
                }
            });
            platform.gameObject.SetActive(true);
            for (int i = 0; i < 16 && firstMoveAt < 0; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(firstMoveAt - startedAt, Is.InRange(0.12f - 0.005f, 0.12f + 2 * Time.fixedDeltaTime + 0.005f));
            Assert.That(platform.transform.position.x, Is.GreaterThan(origin.x));
        }

        [UnityTest]
        public IEnumerator PauseAndStopHoldPositionAcrossFixedUpdatesAndRunRestartsThePath()
        {
            var origin = new Vector3(16000, 16800);
            var platform = NewInactivePlatform(origin, MoveType.Once, 4);
            var pauses = new List<bool>();
            int moves = 0, stops = 0;
            platform.onMove = new Vector3UnityEvent();
            platform.onPause = new BoolUnityEvent();
            platform.onStop = new UnityEvent();
            platform.onMove.AddListener(_ => moves++);
            platform.onPause.AddListener(pauses.Add);
            platform.onStop.AddListener(() => stops++);
            platform.gameObject.SetActive(true);
            yield return new WaitForFixedUpdate();
            Assert.That(platform.transform.position.x, Is.GreaterThan(origin.x));

            platform.Pause(true);
            var pausedAt = platform.transform.position;
            int movesBeforePause = moves;
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(platform.transform.position, Is.EqualTo(pausedAt));
            Assert.That(moves, Is.EqualTo(movesBeforePause));
            Assert.That(platform.IsRunning, Is.False);
            platform.Pause(false);
            yield return new WaitForFixedUpdate();
            Assert.That(platform.transform.position.x, Is.GreaterThan(pausedAt.x));
            Assert.That(pauses, Is.EqualTo(new[] { true, false }));

            platform.Stop();
            var stoppedAt = platform.transform.position;
            int movesBeforeStop = moves;
            platform.Pause(false);
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            Assert.That(platform.IsRunning, Is.False);
            Assert.That(platform.transform.position, Is.EqualTo(stoppedAt));
            Assert.That(moves, Is.EqualTo(movesBeforeStop));
            Assert.That(stops, Is.EqualTo(1));

            float restartedAt = Time.fixedTime;
            platform.Run();
            yield return new WaitForFixedUpdate();
            Assert.That(platform.IsRunning, Is.True);
            Assert.That(platform.transform.position.x - origin.x,
                Is.EqualTo(4 * (Time.fixedTime - restartedAt)).Within(0.005f));
        }

        [UnityTest]
        public IEnumerator RealFixedUpdatesCarryTheContactedRigidbodyWithThePlatform()
        {
            var origin = new Vector3(16000, 16900);
            var platform = NewInactivePlatform(origin, MoveType.Once, 4);
            var catcher = platform.GetComponent<PlatformCatcher2D>();
            catcher.areas = new[] { new Area { from = new Vector3(-2, 1), to = new Vector3(2, 1) } };
            var passenger = NewObject("FixedPassenger", origin + Vector3.up);
            var body = passenger.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            passenger.AddComponent<BoxCollider2D>();
            platform.gameObject.SetActive(true);
            Physics2D.SyncTransforms();
            catcher.RefreshContacts();
            Assert.That(catcher.Contacts.Exists(hit => hit.rigidbody == body), Is.True);
            var before = body.position;
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForFixedUpdate();
            }

            var platformDelta = (Vector2)(platform.transform.position - origin);
            Assert.That(platformDelta.x, Is.GreaterThan(0));
            Assert.That(Vector2.Distance(body.position - before, platformDelta), Is.LessThan(0.005f));
        }
    }
}

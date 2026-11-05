using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YLFramework.Character2D;
using YLFramework.Platform2D;

namespace YLFramework.Movement2D.Acceptance
{
    public sealed class Movement2DPlayerProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class PlayerOwner
        {
            public int schemaVersion;
            public string token;
            public string player;
        }

        [Serializable]
        public sealed class PathReport
        {
            public string mode;
            public int starts;
            public int moves;
            public int stops;
            public int pauses;
            public bool onlyFixedMoves;
            public bool committedCallbacks;
            public bool wrapped;
            public bool reversed;
            public float furthest;
            public float largestStep;
            public float passengerError;
            public float endpoint;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string status;
            public string error;
            public string unityVersion;
            public int pid;
            public bool isEditor;
            public string platform;
            public string backend;
            public int round;
            public string token;
            public string characterAssembly;
            public string platformAssembly;
            public string probeAssembly;
            public string[] frameworkAssemblies;
            public bool serializedPrefab;
            public int persistentListeners;
            public int groundEvents;
            public int wallEvents;
            public int ceilingEvents;
            public int jumpEvents;
            public int landingEvents;
            public int velocityEvents;
            public bool committedCallbacks;
            public bool groundCollider;
            public bool wallCollider;
            public bool ceilingCollider;
            public float jumpRise;
            public bool pauseHeld;
            public bool callbackDisable;
            public bool pooledMotor;
            public bool pooledPlatform;
            public bool released;
            public List<PathReport> paths = new List<PathReport>();
        }

        public GameObject scenarioPrefab;
        public string runToken;
        private Report _report;
        private GameObject _instance;
        private string _runtimeError;

        private IEnumerator Start()
        {
            _report = new Report
            {
                unityVersion = Application.unityVersion,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                isEditor = Application.isEditor,
                platform = Application.platform.ToString(),
#if ENABLE_MONO
                backend = "Mono",
#else
                backend = "NotMono",
#endif
                token = runToken,
                characterAssembly = typeof(CharacterController2D).Assembly.GetName().Name,
                platformAssembly = typeof(MoveablePlatform2D).Assembly.GetName().Name,
                probeAssembly = typeof(Movement2DPlayerProbe).Assembly.GetName().Name
            };
            string result = null;
            var exercise = Exercise();
            Application.logMessageReceived += CaptureError;
            try
            {
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylMovement2DResult");
                    Require(index > 0 && index + 1 < args.Length, "Missing owned Movement2D result argument.");
                    string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    string candidate = Path.GetFullPath(args[index + 1]);
                    Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                        !File.Exists(candidate) && !Directory.Exists(candidate), "Result must be new and inside this Player's owned directory.");
                    NoLinks(directory);
                    string ownerPath = Path.Combine(directory, ".movement2d-owner.json");
                    NoLinks(ownerPath);
                    var owner = JsonUtility.FromJson<PlayerOwner>(File.ReadAllText(ownerPath));
                    Require(Guid.TryParseExact(runToken, "N", out _) && owner != null && owner.schemaVersion == 1 && owner.token == runToken &&
                        string.Equals(Path.GetFullPath(owner.player), directory, StringComparison.OrdinalIgnoreCase), "Player ownership does not match the serialized run token.");
                    result = candidate;
                    _report.round = Path.GetFileName(candidate) == "result-1.json" ? 1 : 2;
                    Require(!_report.isEditor && _report.platform == "WindowsPlayer" && _report.backend == "Mono", "Only a real Windows Mono Player satisfies this fixture.");
                    Time.timeScale = 1;
                    Time.fixedDeltaTime = 0.02f;
                    Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
                    Physics2D.gravity = new Vector2(0, -9.81f);
                    Require(!Physics2D.GetIgnoreLayerCollision(0, 0), "The owned host must enable Default/Default 2D contacts.");
                }
                catch (Exception error)
                {
                    _report.error = error.ToString();
                }
                while (_report.error == null)
                {
                    bool more;
                    try
                    {
                        Require(_runtimeError == null, _runtimeError);
                        more = exercise.MoveNext();
                    }
                    catch (Exception error)
                    {
                        _report.error = error.ToString();
                        break;
                    }
                    if (!more)
                    {
                        _report.status = "Passed";
                        break;
                    }
                    yield return exercise.Current;
                }
            }
            finally
            {
                Time.timeScale = 1;
                Application.logMessageReceived -= CaptureError;
                (exercise as IDisposable)?.Dispose();
                if (_instance)
                {
                    Destroy(_instance);
                }
                _report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                _report.status = _report.status == "Passed" ? "Passed" : "Failed";
                bool persisted = false;
                try
                {
                    if (result != null)
                    {
                        using var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(JsonUtility.ToJson(_report, true));
                        writer.Flush();
                        persisted = true;
                    }
                }
                finally
                {
                    Application.Quit(_report.status == "Passed" && persisted ? 0 : 1);
                }
            }
        }

        private IEnumerator Exercise()
        {
            Require(_report.characterAssembly == "YLFramework.Character2D" && _report.platformAssembly == "YLFramework.Platform2D", "Movement2D types have the wrong Runtime ownership.");
            Require(scenarioPrefab && scenarioPrefab.name == "Movement2DPackageAcceptance" && !scenarioPrefab.activeSelf, "The built scene lost its inactive serialized Prefab.");
            _instance = Instantiate(scenarioPrefab);
            var motor = _instance.GetComponentInChildren<CharacterController2D>(true);
            var receiver = motor.GetComponent<Movement2DEventReceiver>();
            var floor = _instance.transform.Find("Floor").GetComponent<BoxCollider2D>();
            var wall = _instance.transform.Find("Wall").GetComponent<BoxCollider2D>();
            var ceiling = _instance.transform.Find("Ceiling").GetComponent<BoxCollider2D>();
            var platforms = _instance.GetComponentsInChildren<MoveablePlatform2D>(true);
            Require(receiver.motor == motor && platforms.Length == 3 && motor.maxJumpCount == 2 && motor.ChecksWalls && motor.ChecksCeiling, "Prefab fields or component links were not serialized.");
            var characterEvents = new UnityEngine.Events.UnityEventBase[] { motor.onGroundValueChange, motor.onTouchWallValueChange, motor.onTopValueChange, motor.onJumpCountValueChange, motor.onVelocityValueChange };
            foreach (var callback in characterEvents)
            {
                Require(callback != null && callback.GetPersistentEventCount() == 1 && callback.GetPersistentTarget(0) == receiver, "A character persistent event lost its Prefab receiver.");
                _report.persistentListeners++;
            }
            foreach (var platform in platforms)
            {
                var sink = platform.GetComponent<Movement2DEventReceiver>();
                Require(sink.platform == platform && platform.Positions.Count == 2 && platform.Speed == 10, "Serialized platform path or receiver link was lost.");
                foreach (var callback in new UnityEngine.Events.UnityEventBase[] { platform.onStartRun, platform.onMove, platform.onPause, platform.onStop })
                {
                    Require(callback != null && callback.GetPersistentEventCount() == 1 && callback.GetPersistentTarget(0) == sink, "A platform persistent event lost its Prefab receiver.");
                    _report.persistentListeners++;
                }
            }
            _report.serializedPrefab = true;
            _instance.SetActive(true);
            Physics2D.SyncTransforms();
            float deadline = Time.realtimeSinceStartup + 20;
            while (!motor.IsGrounded)
            {
                Require(Time.realtimeSinceStartup < deadline, "Real ground contact did not arrive.");
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            _report.groundCollider = motor.GroundInfo.collider == floor && Vector2.Dot(motor.GroundInfo.normal, Vector2.up) > 0.9f;
            Require(_report.groundCollider, "GroundInfo is not the actual floor hit.");
            while (!motor.IsTouchingWall)
            {
                Require(Time.realtimeSinceStartup < deadline, "Real wall contact did not arrive.");
                motor.MoveHorizontal(2);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            _report.wallCollider = motor.WallInfo.collider == wall && motor.WallInfo.normal.x < -0.9f;
            Require(_report.wallCollider, "WallInfo is not the actual right wall hit.");
            float startY = motor.Rigidbody.position.y;
            motor.Jump(6);
            float peak = startY;
            while (receiver.tops == 0)
            {
                Require(Time.realtimeSinceStartup < deadline, "The real jump never reached the ceiling.");
                yield return new WaitForFixedUpdate();
                yield return null;
                peak = Mathf.Max(peak, motor.Rigidbody.position.y);
            }
            _report.ceilingCollider = motor.TopInfo.collider == ceiling && Vector2.Dot(motor.TopInfo.normal, Vector2.down) > 0.9f;
            _report.jumpRise = peak - startY;
            Require(_report.ceilingCollider && _report.jumpRise > 0.3f, "The actual upward displacement and ceiling hit are required.");
            while (!motor.IsGrounded || motor.CurrentJumpCount != 0)
            {
                Require(Time.realtimeSinceStartup < deadline, "Jump did not land and reset its count.");
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            motor.JumpStart(4, 12);
            yield return new WaitForFixedUpdate();
            yield return null;
            var pausedAt = motor.Rigidbody.position;
            float pausedClock = Time.fixedTime;
            Time.timeScale = 0;
            yield return new WaitForSecondsRealtime(0.08f);
            _report.pauseHeld = Vector2.Distance(pausedAt, motor.Rigidbody.position) < 0.001f && Time.fixedTime == pausedClock;
            Time.timeScale = 1;
            motor.JumpEnd();
            Require(_report.pauseHeld, "A paused physics clock still advanced the motor.");

            receiver.disableOnJump = true;
            // The previous jump may still occupy one slot; allow exactly one callback-boundary attempt.
            motor.maxJumpCount = 3;
            motor.Move(Vector2.right * 3);
            motor.JumpStart(6, 12);
            _report.callbackDisable = !motor.enabled && motor.CurrentJumpCount == 0 && motor.Velocity == Vector2.zero &&
                motor.Rigidbody.gravityScale == 2 && !motor.IsGrounded && !motor.IsTouchingWall && !motor.IsTouchingCeiling;
            Require(_report.callbackDisable, "Disabling from the committed jump callback did not invalidate the remaining jump flow.");
            receiver.disableOnJump = false;
            motor.gameObject.SetActive(false);
            motor.Move(Vector2.right * 10);
            motor.Jump(8);
            var reusedAt = new Vector2(-4, 3);
            motor.transform.position = reusedAt;
            motor.Rigidbody.position = reusedAt;
#if UNITY_6000_0_OR_NEWER
            motor.Rigidbody.linearVelocity = Vector2.zero;
#else
            motor.Rigidbody.velocity = Vector2.zero;
#endif
            motor.gravityScale = 0;
            motor.enabled = true;
            motor.gameObject.SetActive(true);
            Require(motor.CurrentJumpCount == 0 && motor.Velocity == Vector2.zero && !motor.IsGrounded && !motor.IsTouchingWall && !motor.IsTouchingCeiling &&
                !motor.GroundInfo.collider && !motor.WallInfo.collider && !motor.TopInfo.collider, "Pooled reuse retained old contact or jump state.");
            Physics2D.SyncTransforms();
            for (int i = 0; i < 8; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Require(Vector2.Distance(reusedAt, motor.Rigidbody.position) < 0.01f, "Pooled motor committed queued movement or jump from its previous activation.");
            motor.gravityScale = 2;
            while (!motor.IsGrounded)
            {
                Require(Time.realtimeSinceStartup < deadline, "Pooled motor did not reacquire actual ground.");
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            startY = motor.Rigidbody.position.y;
            motor.Jump(5);
            for (int i = 0; i < 6; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            _report.pooledMotor = motor.Rigidbody.position.y > startY + 0.2f && motor.CurrentJumpCount == 1;
            Require(_report.pooledMotor, "Pooled motor no longer accepts a fresh real jump.");
            motor.enabled = false;
            _report.groundEvents = receiver.grounds;
            _report.wallEvents = receiver.walls;
            _report.ceilingEvents = receiver.tops;
            _report.jumpEvents = receiver.jumps;
            _report.landingEvents = receiver.landings;
            _report.velocityEvents = receiver.velocities;
            _report.committedCallbacks = receiver.committed;
            Require(receiver.committed && receiver.grounds >= 2 && receiver.walls > 0 && receiver.tops > 0 && receiver.jumps >= 3 && receiver.landings > 0 && receiver.velocities > 0, "Serialized character events did not execute from real movement.");

            foreach (var platform in platforms.OrderBy(value => (int)value.MoveType))
            {
                var sink = platform.GetComponent<Movement2DEventReceiver>();
                var catcher = platform.GetComponent<PlatformCatcher2D>();
                var passenger = _instance.transform.Find("Passenger" + platform.MoveType).GetComponent<Rigidbody2D>();
                var origin = platform.transform.position;
                var passengerStart = passenger.position;
                catcher.RefreshContacts();
                Require(catcher.Contacts.Count(hit => hit.rigidbody == passenger) >= 2, "Real linecasts must discover duplicate passenger colliders/areas before carrying.");
                platform.Run();
                float error = 0;
                for (int step = 0; step < 26; step++)
                {
                    yield return new WaitForFixedUpdate();
                    yield return null;
                    error = Mathf.Max(error, Vector2.Distance(passenger.position - passengerStart, (Vector2)(platform.transform.position - origin)));
                }
                Require(error < 0.03f && sink.onlyFixedMoves && sink.committed && sink.furthest > 1.5f, "Fixed path traversal or once-per-body carrying diverged.");
                if (platform.MoveType == MoveType.Loop)
                {
                    Require(sink.wrapped && platform.IsRunning, "Loop must actually wrap, not stop or reverse.");
                }
                else if (platform.MoveType == MoveType.PingPong)
                {
                    Require(sink.reversed && !sink.wrapped && sink.largestStep <= 0.21f && platform.IsRunning, "PingPong must reverse continuously.");
                }
                else
                {
                    Require(!platform.IsRunning && sink.stops == 1 && Mathf.Abs(platform.transform.position.x - origin.x - 2) < 0.001f, "Once must commit its endpoint and stop once.");
                }
                if (platform.MoveType == MoveType.Once)
                {
                    platform.Run();
                }
                platform.Pause(true);
                var held = platform.transform.position;
                int moves = sink.moves;
                for (int step = 0; step < 3; step++)
                {
                    yield return new WaitForFixedUpdate();
                    yield return null;
                }
                Require(platform.transform.position == held && sink.moves == moves && !platform.IsRunning, "Paused path committed movement.");
                platform.Pause(false);
                platform.Stop();
                _report.paths.Add(new PathReport
                {
                    mode = platform.MoveType.ToString(),
                    starts = sink.starts,
                    moves = sink.moves,
                    stops = sink.stops,
                    pauses = sink.pauses,
                    onlyFixedMoves = sink.onlyFixedMoves,
                    committedCallbacks = sink.committed,
                    wrapped = sink.wrapped,
                    reversed = sink.reversed,
                    furthest = sink.furthest,
                    largestStep = sink.largestStep,
                    passengerError = error,
                    endpoint = held.x - origin.x
                });
            }
            var reusable = platforms.Single(value => value.MoveType == MoveType.Once);
            var reusableCatcher = reusable.GetComponent<PlatformCatcher2D>();
            var oldPassenger = _instance.transform.Find("PassengerOnce").GetComponent<Rigidbody2D>();
            var replacement = new GameObject("OwnedReplacementPassenger");
            replacement.transform.SetParent(_instance.transform, false);
            replacement.transform.position = reusable.transform.position + Vector3.up;
            var replacementBody = replacement.AddComponent<Rigidbody2D>();
            replacementBody.gravityScale = 0;
            replacement.AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 0.8f);
            reusable.gameObject.SetActive(false);
            Require(reusableCatcher.Contacts.Count == 0, "Pooling a platform must discard old candidates.");
            oldPassenger.position += Vector2.up * 10;
            var oldPosition = oldPassenger.position;
            reusable.gameObject.SetActive(true);
            Physics2D.SyncTransforms();
            reusableCatcher.RefreshContacts();
            Require(reusableCatcher.Contacts.Any(hit => hit.rigidbody == replacementBody) && reusableCatcher.Contacts.All(hit => hit.rigidbody != oldPassenger), "Reused catcher retained the old passenger.");
            var replacementStart = replacementBody.position;
            var platformStart = reusable.transform.position;
            reusable.Run();
            for (int step = 0; step < 3; step++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            _report.pooledPlatform = Vector2.Distance(oldPassenger.position, oldPosition) < 0.001f &&
                Vector2.Distance(replacementBody.position - replacementStart, (Vector2)(reusable.transform.position - platformStart)) < 0.03f &&
                Vector3.Distance(reusable.transform.position, platformStart) > 0.1f;
            Require(_report.pooledPlatform, "Reused platform did not exclusively carry its new physical passenger.");
            Destroy(_instance);
            yield return null;
            yield return null;
            _report.released = !_instance && !motor && !reusable && !replacementBody;
            Require(_report.released && _runtimeError == null, "Owned fixture objects were not destroyed cleanly.");
        }

        private void CaptureError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _runtimeError = message + "\n" + stack;
            }
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
            {
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Linked Player file: " + path);
            }
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Linked Player directory: " + parent.FullName);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

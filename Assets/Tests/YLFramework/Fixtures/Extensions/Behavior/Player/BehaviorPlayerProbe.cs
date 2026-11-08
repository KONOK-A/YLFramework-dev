using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using YLFramework.Character2D;
using YLFramework.FSM;
using YLFramework.Overlap;
using YLFramework.Platform2D;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    // 仅由专属验收场景使用；无自动启动、全局注册或产品场景引用。
    public sealed class BehaviorPlayerProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class PlayerResult
        {
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string catalogDigest = string.Empty;
            public int pid;
            public int leaseCount;
            public int recordCount;
        }

        public RuntimeFsmController source;
        public string sourceGuid;
        public bool expectedDevelopment;
        public BehaviorEventReceiver eventReceiver;
        public CharacterController2D eventMotor;
        public Moveable eventPlatform;
        public OverlapDetector2D eventQuery;
        public OverlapDetector3D eventSphere;
        public Collider2D eventTarget;
        public Collider eventTarget3D;
        private Fsm<int> _lightweight;
        private int _stateCreates;
        private int _stateEnters;
        private int _stateExits;
        private string _result = "Not run";

        public void RecordState(string phase)
        {
            if (phase == "create")
            {
                _stateCreates++;
            }
            else if (phase == "enter")
            {
                _stateEnters++;
            }
            else
            {
                _stateExits++;
            }
        }

        private GameObject Own(string name, Vector3 position, bool active = true)
        {
            var obj = new GameObject("YL.Acceptance." + name);
            obj.transform.SetParent(transform, false);
            obj.transform.position = position;
            obj.SetActive(active);
            return obj;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private IEnumerator Start()
        {
            var run = Run();
            Exception failure = null;
            string runtimeError = null;
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    runtimeError = message + "\n" + stack;
            };
            Application.logMessageReceived += capture;
            while (true)
            {
                bool hasNext;
                try
                {
                    Require(runtimeError == null, runtimeError);
                    hasNext = run.MoveNext();
                    Require(runtimeError == null, runtimeError);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    break;
                }
                if (!hasNext)
                {
                    break;
                }

                yield return run.Current;
            }
            Application.logMessageReceived -= capture;
            _result = failure == null ? "PASS" : "FAIL: " + failure.Message;
#if ENABLE_IL2CPP
            const string Backend = "IL2CPP";
#else
            const string Backend = "Mono";
#endif
            string events = eventReceiver ? $"hit2D:{eventReceiver.hitCount},hit3D:{eventReceiver.hit3DCount},bool:{eventReceiver.boolCount},ground:{eventReceiver.groundTrueCount},wall:{eventReceiver.wallTrueCount},top:{eventReceiver.topTrueCount},int:{eventReceiver.intCount},vector2:{eventReceiver.vector2Count},vector3:{eventReceiver.vector3Count}" : "missing";
            string report = $"YL_BEHAVIOR_ACCEPTANCE result={_result}; unity={Application.unityVersion}; backend={Backend}; development={Debug.isDebugBuild}; sourceGuid={sourceGuid}; creates={_stateCreates}; enters={_stateEnters}; exits={_stateExits}; events={events}; graphics={SystemInfo.graphicsDeviceType}; dataPath={Application.persistentDataPath}";
            if (failure == null)
            {
                Debug.Log(report);
            }
            else
            {
                Debug.LogError(report);
                Debug.LogException(failure);
            }
            if (!Application.isEditor)
            {
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylFeatureResult");
                    Require(index >= 0 && index + 1 < args.Length, "Missing owned shared Player result path.");
                    string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    string result = Path.GetFullPath(args[index + 1]);
                    Require(string.Equals(Path.GetDirectoryName(result), directory, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(result) == "result-1.json" || Path.GetFileName(result) == "result-2.json") &&
                        !File.Exists(result) && !Directory.Exists(result), "Result must be a new child of the actual Player directory.");
                    for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                        Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Player result cannot cross a reparse point.");
                    var value = new PlayerResult
                    {
                        status = failure == null ? "Passed" : "Failed",
                        error = failure?.ToString(),
                        unityVersion = Application.unityVersion,
                        platform = Application.platform.ToString(),
                        isEditor = Application.isEditor,
                        runtimeAssembly = typeof(OverlapDetector2D).Assembly.GetName().Name,
                        pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        leaseCount = 0,
                        recordCount = 0
                    };
                    using var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.Write(JsonUtility.ToJson(value, true));
                }
                catch (Exception error)
                {
                    failure = error;
                    Debug.LogException(error);
                }
                Application.Quit(failure == null ? 0 : 1);
            }
        }

        private IEnumerator Run()
        {
            Require(source && !string.IsNullOrEmpty(sourceGuid), "Generate the dedicated fixture asset and scene before running.");
            Require(eventReceiver && eventMotor && eventPlatform && eventQuery && eventSphere && eventTarget && eventTarget3D, "Serialized generic-event fixture references were not saved/reloaded.");
            Require(global::YLFramework.Resource.ResourceSystem.Current == null, "Behavior Player must not publish or borrow a Resource system.");
            if (!Application.isEditor)
            {
                Require(Debug.isDebugBuild == expectedDevelopment, "Player build flavor differs from the generated fixture.");
            }

            var origin = new Vector3(3200, 3200);
            var target = Own("OverlapTarget", origin).AddComponent<BoxCollider2D>();
            var query = Own("Overlap", origin).AddComponent<OverlapDetector2D>();
            query.Composite = new OverlapComposite2D(OverlapShape2D.Circle(Vector2.zero, 2));
            query.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 });
            int enters = 0, exits = 0;
            query.Entered += _ => enters++;
            query.Exited += (_, __) => exits++;
            Physics2D.SyncTransforms();
            Require(query.Sample() && query.LastComplete.Count == 1 && enters == 1, "Overlap2D enter/contact failed.");
            target.transform.position += Vector3.up * 10;
            Physics2D.SyncTransforms();
            Require(query.Sample() && query.LastComplete.Count == 0 && exits == 1, "Overlap2D exit failed.");

            var spherePosition = origin + Vector3.forward * 20;
            Own("3DTarget", spherePosition).AddComponent<BoxCollider>();
            var sphere = Own("Sphere", spherePosition).AddComponent<OverlapDetector3D>();
            sphere.Composite = new OverlapComposite3D(OverlapShape3D.Sphere(Vector3.zero, 2));
            sphere.Filter = new OverlapFilter3D(1);
            Physics.SyncTransforms();
            Require(sphere.Sample() && sphere.LastComplete.Count == 1, "Overlap3D failed.");

            var motorPosition = origin + Vector3.right * 5;
            var floor = Own("Floor", motorPosition + Vector3.down * 1.5f).AddComponent<BoxCollider2D>();
            floor.size = new Vector2(4, 1);
            var motorObject = Own("Motor", motorPosition, false);
            motorObject.AddComponent<Rigidbody2D>().gravityScale = 2;
            motorObject.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
            var motor = motorObject.AddComponent<CharacterController2D>();
            motorObject.SetActive(true);

            var platformPosition = origin + Vector3.right * 12;
            var platformObject = Own("Platform", platformPosition, false);
            platformObject.AddComponent<BoxCollider2D>().size = new Vector2(4, 1);
            var platform = platformObject.AddComponent<MoveablePlatform2D>();
            platform.Positions.Add(platformPosition);
            platform.Positions.Add(platformPosition + Vector3.right);
            platform.Speed = 2;
            platform.MoveType = MoveType.Once;
            var catcher = platformObject.GetComponent<PlatformCatcher2D>();
            catcher.areas = new[] { new Area { from = new Vector3(-1.5f, 1), to = new Vector3(1.5f, 1) } };
            var passenger = Own("Passenger", platformPosition + Vector3.up);
            var passengerBody = passenger.AddComponent<Rigidbody2D>();
            passengerBody.gravityScale = 0;
            passenger.AddComponent<BoxCollider2D>();
            passenger.AddComponent<CircleCollider2D>().radius = 0.25f;
            var passengerStart = passengerBody.position;
            platform.onMove = new Vector3UnityEvent();
            platform.onMove.AddListener(_ =>
            {
                Physics2D.SyncTransforms();
                catcher.RefreshContacts();
            });
            platformObject.SetActive(true);

            var assetObject = Own("AssetFSM", origin, false);
            var asset = assetObject.AddComponent<FsmController>();
            asset.UserData = this;
            asset.AddRuntimeFsmController(source, "Acceptance");
            assetObject.SetActive(true);
            _lightweight = Fsm<int>.Allocate().Init(null);
            _lightweight.State(1);
            _lightweight.State(2);
            Require(_lightweight.SetUp(1), "Lightweight FSM startup failed.");
            yield return null;
            Require(asset.GetCurrentStateInfo(0)?.Data.name == "Idle", "Asset FSM startup failed.");
            asset.SetBool("Go", true);
            Require(asset.GetCurrentStateInfo(0)?.Data.name == "Active" && _lightweight.CurrentStateId == 1, "Independent asset transition failed.");
            Require(_lightweight.ChangeState(2), "Lightweight FSM transition failed.");
            asset.SetTrigger("Fire");
            asset.SetTrigger("Fire");
            Require(asset.GetTrigger("Fire"), "Trigger/queue failed.");
            asset.ResetTrigger("Fire");
            Require(!asset.GetTrigger("Fire"), "Trigger reset failed.");

            for (int i = 0; i < 80; i++)
            {
                motor.MoveHorizontal(0);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Require(motor.IsGrounded && motor.GroundInfo.collider == floor, "CC2D real ground contact failed.");
            var carriedDelta = passengerBody.position - passengerStart;
            Require(!platform.IsRunning && Vector2.Distance(carriedDelta, Vector2.right) < 0.05f, "Carrier displacement or Rigidbody deduplication failed.");
            eventReceiver.gameObject.SetActive(true);
            Physics2D.SyncTransforms();
            Physics.SyncTransforms();
            Require(eventQuery.Sample() && eventSphere.Sample(), "Persistent Detector sample was invalid.");
            Require(eventReceiver.hitCount == 1 && eventReceiver.hit3DCount == 1, "Persistent Overlap2D/3D enter events did not execute in Player.");
            Require(eventQuery.Sample() && eventSphere.Sample() && eventReceiver.stayCount == 1 && eventReceiver.stay3DCount == 1,
                "Persistent Overlap2D/3D stay events did not execute in Player.");
            Require(eventReceiver.point.z == 0 && eventReceiver.point3D.z == 20, "Contact points lost their 2D/3D dimensions.");
            eventTarget.transform.position += Vector3.up * 10;
            eventTarget3D.transform.position += Vector3.up * 10;
            Physics2D.SyncTransforms();
            Physics.SyncTransforms();
            Require(eventQuery.Sample() && eventSphere.Sample(), "Persistent exit sample was invalid.");
            Require(eventReceiver.hitCount == 2 && eventReceiver.hit3DCount == 2, "Persistent Overlap2D/3D exit events did not execute in Player.");
            Require(eventReceiver.exitReason == OverlapExitReason.Separated && eventReceiver.exitReason3D == OverlapExitReason.Separated &&
                eventReceiver.exitPoint == eventReceiver.point && eventReceiver.exitPoint3D == eventReceiver.point3D,
                "Persistent exit reason or last committed point was lost.");
            for (int i = 0; i < 40; i++)
            {
                eventMotor.MoveHorizontal(0);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Require(eventMotor.IsGrounded && eventReceiver.groundTrueCount > 0 && eventReceiver.wallTrueCount > 0, "Persistent CC2D ground/wall Bool events did not execute from real contacts.");
            eventMotor.Jump(6);
            for (int i = 0; i < 80; i++)
            {
                eventMotor.MoveHorizontal(0);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Require(eventReceiver.boolCount >= 2 && eventReceiver.topTrueCount > 0, "Persistent CC2D airborne/top Bool events did not execute from a real jump.");
            Require(eventReceiver.intCount > 0 && eventReceiver.positiveIntCount > 0, "Persistent CC2D Int event did not execute from Jump.");
            Require(eventReceiver.vector2Count > 0 && eventReceiver.nonzeroVector2Count > 0, "Persistent CC2D Vector2 event did not execute from real movement.");
            Require(!eventPlatform.IsRunning && eventReceiver.vector3Count > 0 && eventReceiver.nonzeroVector3Count > 0, "Persistent platform Vector3 event did not execute from FixedUpdate.");
            eventMotor.enabled = false;
            Require(eventMotor.Rigidbody.gravityScale == 2, "Serialized motor did not return gravity ownership.");
            motor.enabled = false;
            Require(motor.Rigidbody.gravityScale == 2, "CC2D gravity ownership was not restored.");
            asset.enabled = false;
            Require(_stateCreates == 2 && _stateEnters == 2 && _stateExits == 2, "State create/enter/exit lifecycle failed.");
            Require(source.parameters[0].Value == 0 && _lightweight.CurrentStateId == 2, "Source asset or lightweight FSM was mutated by the asset FSM.");
            _lightweight.RecycleToCache();
            _lightweight = null;
            Require(global::YLFramework.Resource.ResourceSystem.Current == null, "Behavior must leave Resource unpublished after all consumers finish.");
        }

        private void OnGUI() => GUI.Label(new Rect(20, 20, 900, 60), "YL Behavior Acceptance: " + _result);

        private void OnDestroy()
        {
            if (_lightweight != null)
            {
                _lightweight.RecycleToCache();
                _lightweight = null;
            }
        }
    }
}

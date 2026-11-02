using System;
using System.Collections;
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
        public RuntimeFsmController source;
        public string sourceGuid;
        public bool expectedDevelopment;
        public BehaviorEventReceiver eventReceiver;
        public CharacterController2D eventMotor;
        public Moveable eventPlatform;
        public OverlapCircle2D eventQuery;
        public OverlapSphere3D eventSphere;
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
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = run.MoveNext();
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
                Application.Quit(failure == null ? 0 : 1);
            }
        }

        private IEnumerator Run()
        {
            Require(source && !string.IsNullOrEmpty(sourceGuid), "Generate the dedicated fixture asset and scene before running.");
            Require(eventReceiver && eventMotor && eventPlatform && eventQuery && eventSphere && eventTarget && eventTarget3D, "Serialized generic-event fixture references were not saved/reloaded.");
            if (!Application.isEditor)
            {
                Require(Debug.isDebugBuild == expectedDevelopment, "Player build flavor differs from the generated fixture.");
            }

            var origin = new Vector3(3200, 3200);
            var target = Own("OverlapTarget", origin).AddComponent<BoxCollider2D>();
            var query = Own("Overlap", origin).AddComponent<OverlapCircle2D>();
            query.IsRunning = false;
            query.Radius = 2;
            query.LayerMask = 1;
            query.onColliderEnter = new OverlapEvent2D();
            query.onColliderExit = new OverlapEvent2D();
            int enters = 0, exits = 0;
            query.onColliderEnter.AddListener(_ => enters++);
            query.onColliderExit.AddListener(_ => exits++);
            Physics2D.SyncTransforms();
            query.CheckOverlap();
            Require(query.results.Count == 1 && enters == 1, "Overlap2D enter/Results failed.");
            target.transform.position += Vector3.up * 10;
            Physics2D.SyncTransforms();
            query.CheckOverlap();
            Require(query.results.Count == 0 && exits == 1, "Overlap2D exit failed.");

            var spherePosition = origin + Vector3.forward * 20;
            Own("3DTarget", spherePosition).AddComponent<BoxCollider>();
            var sphere = Own("Sphere", spherePosition).AddComponent<OverlapSphere3D>();
            sphere.IsRunning = false;
            sphere.Radius = 2;
            sphere.LayerMask = 1;
            Physics.SyncTransforms();
            sphere.CheckOverlap();
            Require(sphere.results.Count == 1, "Overlap3D failed.");

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
            eventQuery.CheckOverlap();
            eventSphere.CheckOverlap();
            Require(eventReceiver.hitCount == 1 && eventReceiver.hit3DCount == 1, "Persistent Overlap2D/3D enter events did not execute in Player.");
            eventTarget.transform.position += Vector3.up * 10;
            eventTarget3D.transform.position += Vector3.up * 10;
            Physics2D.SyncTransforms();
            Physics.SyncTransforms();
            eventQuery.CheckOverlap();
            eventSphere.CheckOverlap();
            Require(eventReceiver.hitCount == 2 && eventReceiver.hit3DCount == 2, "Persistent Overlap2D/3D exit events did not execute in Player.");
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

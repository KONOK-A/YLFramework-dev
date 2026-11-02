using UnityEngine;
using YLFramework.FSM;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    // 只用于同一Editor进程反复Play的自有场景，不给产品FSM增加驱动层。
    public sealed class BehaviorReloadProbe : MonoBehaviour
    {
        public FsmController asset;
        public RuntimeFsmController source;
        private Fsm<int> _lightweight;
        public bool LightweightAllocated => _lightweight != null;
        public int LightweightState => _lightweight == null ? -1 : _lightweight.CurrentStateId;

        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            _lightweight = Fsm<int>.Allocate().Init(null);
            _lightweight.State(1);
            _lightweight.State(2);
            _lightweight.SetUp(1);
        }

        private void Update() => _lightweight?.Update();
        private void FixedUpdate() => _lightweight?.FixedUpdate();
        private void LateUpdate() => _lightweight?.LateUpdate();
        public bool ChangeLightweight() => _lightweight != null && _lightweight.ChangeState(2);

        private void OnDisable()
        {
            if (_lightweight == null)
            {
                return;
            }

            _lightweight.RecycleToCache();
            _lightweight = null;
        }
    }
}

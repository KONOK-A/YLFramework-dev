using UnityEngine.Scripting;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    [Preserve]
    public sealed class BehaviorAcceptanceState : FsmState
    {
        public override void OnCreate() => (UserData as BehaviorPlayerProbe)?.RecordState("create");
        public override void OnEnter() => (UserData as BehaviorPlayerProbe)?.RecordState("enter");
        public override void OnExit() => (UserData as BehaviorPlayerProbe)?.RecordState("exit");
    }
}

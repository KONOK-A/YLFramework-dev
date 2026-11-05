using UnityEngine.Scripting;

namespace YLFramework.StateMachine.Acceptance
{
    [Preserve]
    public sealed class StateMachinePlayerState : FsmState
    {
        public override void OnCreate() => ((StateMachinePlayerProbe)UserData).Record(this, "create");
        public override void OnEnter() => ((StateMachinePlayerProbe)UserData).Record(this, "enter");
        public override void OnUpdate() => ((StateMachinePlayerProbe)UserData).Record(this, "update");
        public override void OnExit() => ((StateMachinePlayerProbe)UserData).Record(this, "exit");
    }
}

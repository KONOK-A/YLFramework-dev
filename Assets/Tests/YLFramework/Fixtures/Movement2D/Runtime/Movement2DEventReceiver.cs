using System;
using UnityEngine;
using YLFramework.Character2D;
using YLFramework.Platform2D;

namespace YLFramework.Movement2D.Acceptance
{
    public sealed class Movement2DEventReceiver : MonoBehaviour
    {
        public CharacterController2D motor;
        public MoveablePlatform2D platform;
        [NonSerialized] public int grounds;
        [NonSerialized] public int walls;
        [NonSerialized] public int tops;
        [NonSerialized] public int jumps;
        [NonSerialized] public int landings;
        [NonSerialized] public int velocities;
        [NonSerialized] public int starts;
        [NonSerialized] public int moves;
        [NonSerialized] public int pauses;
        [NonSerialized] public int stops;
        [NonSerialized] public bool committed = true;
        [NonSerialized] public bool onlyFixedMoves = true;
        [NonSerialized] public bool disableOnJump;
        [NonSerialized] public bool wrapped;
        [NonSerialized] public bool reversed;
        [NonSerialized] public float furthest;
        [NonSerialized] public float largestStep;
        private float _previous;
        private bool _hasTarget;
        private Vector3 _lastTarget;

        public void OnGround(bool value)
        {
            committed &= motor && motor.IsGrounded == value;
            if (value)
            {
                grounds++;
            }
        }

        public void OnWall(bool value)
        {
            committed &= motor && motor.IsTouchingWall == value;
            if (value)
            {
                walls++;
            }
        }

        public void OnTop(bool value)
        {
            committed &= motor && motor.IsTouchingCeiling == value;
            if (value)
            {
                tops++;
            }
        }

        public void OnJump(int value)
        {
            committed &= motor && motor.CurrentJumpCount == value;
            if (value > 0)
            {
                jumps++;
                if (disableOnJump)
                {
                    motor.enabled = false;
                }
            }
            else
            {
                landings++;
            }
        }

        public void OnVelocity(Vector2 value)
        {
            committed &= motor && motor.Velocity == value;
            if (value.sqrMagnitude > 0.01f)
            {
                velocities++;
            }
        }

        public void OnStart()
        {
            starts++;
            committed &= platform && platform.IsRunning;
        }

        public void OnMove(Vector3 target)
        {
            moves++;
            onlyFixedMoves &= Time.inFixedTimeStep;
            if (_hasTarget)
            {
                committed &= Vector3.Distance(platform.transform.position, _lastTarget) < 0.005f;
            }
            _hasTarget = true;
            _lastTarget = target;
            float position = platform.transform.position.x - platform.Positions[0].x;
            wrapped |= _previous > 1.5f && position < 0.5f;
            reversed |= furthest >= 1.8f && position < _previous - 0.01f;
            furthest = Mathf.Max(furthest, position);
            if (moves > 1)
            {
                largestStep = Mathf.Max(largestStep, Mathf.Abs(position - _previous));
            }
            _previous = position;
            committed &= platform.IsRunning && !float.IsNaN(target.x) && !float.IsInfinity(target.x) &&
                !float.IsNaN(target.y) && !float.IsInfinity(target.y) && !float.IsNaN(target.z) && !float.IsInfinity(target.z);
        }

        public void OnPause(bool value)
        {
            pauses++;
            committed &= platform && platform.IsRunning == !value;
        }

        public void OnStop()
        {
            stops++;
            committed &= platform && !platform.IsRunning;
            if (_hasTarget)
            {
                committed &= Vector3.Distance(platform.transform.position, _lastTarget) < 0.005f;
            }
        }
    }
}

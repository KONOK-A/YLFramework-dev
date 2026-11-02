using UnityEngine;
using UnityEngine.Scripting;
using YLFramework.Overlap;

namespace YLFramework.Behavior.Acceptance
{
    [Preserve]
    public sealed class BehaviorEventReceiver : MonoBehaviour
    {
        public int hitCount;
        public int boolCount;
        public int intCount;
        public int vector2Count;
        public int vector3Count;
        public int hit3DCount;
        public int groundTrueCount;
        public int wallTrueCount;
        public int topTrueCount;
        public int positiveIntCount;
        public int nonzeroVector2Count;
        public int nonzeroVector3Count;
        public void ReceiveHit(OverlapHit<Collider2D> value) => hitCount++;
        public void ReceiveHit3D(OverlapHit<Collider> value) => hit3DCount++;
        public void ReceiveBool(bool value)
        {
            boolCount++;
            if (value)
            {
                groundTrueCount++;
            }
        }
        public void ReceiveWall(bool value)
        {
            if (value)
            {
                wallTrueCount++;
            }
        }
        public void ReceiveTop(bool value)
        {
            if (value)
            {
                topTrueCount++;
            }
        }
        public void ReceiveInt(int value)
        {
            intCount++;
            if (value > 0)
            {
                positiveIntCount++;
            }
        }
        public void ReceiveVector2(Vector2 value)
        {
            vector2Count++;
            if (value.sqrMagnitude > 0)
            {
                nonzeroVector2Count++;
            }
        }
        public void ReceiveVector3(Vector3 value)
        {
            vector3Count++;
            if (value.sqrMagnitude > 0)
            {
                nonzeroVector3Count++;
            }
        }
    }
}

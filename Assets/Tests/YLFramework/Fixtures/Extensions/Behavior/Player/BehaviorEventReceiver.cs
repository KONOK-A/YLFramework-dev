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
        public int stayCount;
        public int stay3DCount;
        public Vector3 point;
        public Vector3 point3D;
        public Vector3 exitPoint;
        public Vector3 exitPoint3D;
        public OverlapExitReason exitReason;
        public OverlapExitReason exitReason3D;
        public void ReceiveHit(OverlapContact<Collider2D> value)
        {
            hitCount++;
            point = value.Point;
        }
        public void ReceiveStay(OverlapContact<Collider2D> value)
        {
            stayCount++;
            point = value.Point;
        }
        public void ReceiveExit(OverlapContact<Collider2D> value, OverlapExitReason reason)
        {
            hitCount++;
            exitPoint = value.Point;
            exitReason = reason;
        }
        public void ReceiveHit3D(OverlapContact<Collider> value)
        {
            hit3DCount++;
            point3D = value.Point;
        }
        public void ReceiveStay3D(OverlapContact<Collider> value)
        {
            stay3DCount++;
            point3D = value.Point;
        }
        public void ReceiveExit3D(OverlapContact<Collider> value, OverlapExitReason reason)
        {
            hit3DCount++;
            exitPoint3D = value.Point;
            exitReason3D = reason;
        }
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

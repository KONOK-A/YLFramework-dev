using UnityEngine;
using UnityEngine.Scripting;
using System.Collections.Generic;

namespace YLFramework.Overlap.Acceptance
{
    [Preserve]
    public sealed class OverlapPlayerEventReceiver : MonoBehaviour
    {
        public int entered2D;
        public int stayed2D;
        public int exited2D;
        public int entered3D;
        public int stayed3D;
        public int exited3D;
        public Vector3 point2D;
        public Vector3 point3D;
        public Vector3 exitPoint2D;
        public Vector3 exitPoint3D;
        public OverlapExitReason reason2D;
        public OverlapExitReason reason3D;
        [System.NonSerialized] public Behaviour lifecycleOwner;
        [System.NonSerialized] public string lifecyclePhase;
        [System.NonSerialized] public string lifecycleCommand;
        public readonly List<string> lifecycleTrace = new List<string>();
        private bool _lifecycleRequested;

        public void BeginLifecycle(Behaviour owner, string phase, string command)
        {
            lifecycleOwner = owner;
            lifecyclePhase = phase;
            lifecycleCommand = command;
            _lifecycleRequested = false;
            lifecycleTrace.Clear();
        }

        public void EndLifecycle() => lifecycleOwner = null;

        private void Primary(bool threeD, string phase)
        {
            if (!lifecycleOwner || (lifecycleOwner is OverlapDetector3D) != threeD || lifecyclePhase != phase)
                return;
            lifecycleTrace.Add(phase + ".A");
            if (_lifecycleRequested)
                return;
            _lifecycleRequested = true;
            if (lifecycleCommand == "disable")
                lifecycleOwner.enabled = false;
            else if (lifecycleOwner is OverlapDetector2D two)
            {
                two.Stop();
                if (lifecycleCommand == "restart")
                    two.StartDetection();
            }
            else if (lifecycleOwner is OverlapDetector3D three)
            {
                three.Stop();
                if (lifecycleCommand == "restart")
                    three.StartDetection();
            }
        }

        private void Peer(bool threeD, string phase)
        {
            if (lifecycleOwner && (lifecycleOwner is OverlapDetector3D) == threeD && lifecyclePhase == phase)
                lifecycleTrace.Add(phase + ".B");
        }

        private void Released(bool threeD, OverlapExitReason reason)
        {
            if (!ReferenceEquals(lifecycleOwner, null) && (lifecycleOwner is OverlapDetector3D) == threeD)
                lifecycleTrace.Add("exit:" + reason);
        }

        public void PeerEnter2D(OverlapContact<Collider2D> contact) => Peer(false, "enter");
        public void PeerStay2D(OverlapContact<Collider2D> contact) => Peer(false, "stay");
        public void PeerEnter3D(OverlapContact<Collider> contact) => Peer(true, "enter");
        public void PeerStay3D(OverlapContact<Collider> contact) => Peer(true, "stay");

        public void Enter2D(OverlapContact<Collider2D> contact)
        {
            entered2D++;
            point2D = contact.Point;
            Primary(false, "enter");
        }

        public void Stay2D(OverlapContact<Collider2D> contact)
        {
            stayed2D++;
            point2D = contact.Point;
            Primary(false, "stay");
        }

        public void Exit2D(OverlapContact<Collider2D> contact, OverlapExitReason reason)
        {
            exited2D++;
            exitPoint2D = contact.Point;
            reason2D = reason;
            Released(false, reason);
        }

        public void Enter3D(OverlapContact<Collider> contact)
        {
            entered3D++;
            point3D = contact.Point;
            Primary(true, "enter");
        }

        public void Stay3D(OverlapContact<Collider> contact)
        {
            stayed3D++;
            point3D = contact.Point;
            Primary(true, "stay");
        }

        public void Exit3D(OverlapContact<Collider> contact, OverlapExitReason reason)
        {
            exited3D++;
            exitPoint3D = contact.Point;
            reason3D = reason;
            Released(true, reason);
        }
    }
}

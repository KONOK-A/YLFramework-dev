using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>Explicit layer and trigger policy; UseGlobal is deliberately not supported.</summary>
    public readonly struct OverlapFilter3D
    {
        private readonly bool _includeTriggers;

        public LayerMask LayerMask
        {
            get;
        }
        public QueryTriggerInteraction TriggerInteraction =>
            _includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
        public Collider IgnoredCollider
        {
            get;
        }
        public Transform IgnoredRoot
        {
            get;
        }
        public bool GroupByRigidbody
        {
            get;
        }
        public bool SortByDistance
        {
            get;
        }

        public OverlapFilter3D(LayerMask layerMask, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore,
            Collider ignoredCollider = null, Transform ignoredRoot = null,
            bool groupByRigidbody = false, bool sortByDistance = false)
        {
            if (triggerInteraction != QueryTriggerInteraction.Ignore && triggerInteraction != QueryTriggerInteraction.Collide)
            {
                throw new ArgumentOutOfRangeException(nameof(triggerInteraction), "Choose Ignore or Collide explicitly.");
            }
            LayerMask = layerMask;
            _includeTriggers = triggerInteraction == QueryTriggerInteraction.Collide;
            IgnoredCollider = ignoredCollider;
            IgnoredRoot = ignoredRoot;
            GroupByRigidbody = groupByRigidbody;
            SortByDistance = sortByDistance;
        }

        internal bool Allows(Collider collider) => collider &&
            (!IgnoredCollider || collider != IgnoredCollider) &&
            (!IgnoredRoot || !collider.transform.IsChildOf(IgnoredRoot));
    }
}

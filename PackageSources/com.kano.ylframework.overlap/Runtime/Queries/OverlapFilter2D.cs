using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>Per-query policy. Destroyed ignored objects are treated as absent, using Unity fake-null.</summary>
    public readonly struct OverlapFilter2D
    {
        public ContactFilter2D ContactFilter
        {
            get;
        }
        public Collider2D IgnoredCollider
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

        public OverlapFilter2D(ContactFilter2D contactFilter, Collider2D ignoredCollider = null,
            Transform ignoredRoot = null, bool groupByRigidbody = false, bool sortByDistance = false)
        {
            ContactFilter = contactFilter;
            IgnoredCollider = ignoredCollider;
            IgnoredRoot = ignoredRoot;
            GroupByRigidbody = groupByRigidbody;
            SortByDistance = sortByDistance;
            Validate();
        }

        internal void Validate()
        {
            if (ContactFilter.useDepth && (float.IsNaN(ContactFilter.minDepth) ||
                float.IsNaN(ContactFilter.maxDepth) || ContactFilter.minDepth > ContactFilter.maxDepth))
            {
                throw new ArgumentOutOfRangeException("contactFilter", "Depth bounds must be ordered and not NaN.");
            }
            if (ContactFilter.useNormalAngle && (!OverlapShape2D.IsFinite(ContactFilter.minNormalAngle) ||
                !OverlapShape2D.IsFinite(ContactFilter.maxNormalAngle) || ContactFilter.minNormalAngle < 0 ||
                ContactFilter.maxNormalAngle > 360 || ContactFilter.minNormalAngle > ContactFilter.maxNormalAngle))
            {
                throw new ArgumentOutOfRangeException("contactFilter", "Normal angles must be ordered within [0, 360].");
            }
        }

        internal bool Allows(Collider2D collider) => collider &&
            (!IgnoredCollider || collider != IgnoredCollider) &&
            (!IgnoredRoot || !collider.transform.IsChildOf(IgnoredRoot));
    }
}

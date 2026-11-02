using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 位置同步扩展：From 系列把源对象的位置写入自身；To 系列把自身位置写入目标对象。
    /// 2D 版本只覆盖 x/y，保留目标自身的 z。所有方法均返回自身以支持链式调用。
    /// </summary>
    public static class PositionSyncExtension
    {
        #region SyncPositionFrom

        /// <summary>把 from 的世界坐标写入自身。</summary>
        public static GameObject SyncPositionFrom(this GameObject self, GameObject from)
        {
            return self.Position(from.Position());
        }

        /// <summary>把 from 的世界坐标写入自身。</summary>
        public static T SyncPositionFrom<T>(this T self, GameObject from) where T : Component
        {
            return self.Position(from.Position());
        }

        /// <summary>把 from 的世界坐标写入自身。</summary>
        public static GameObject SyncPositionFrom(this GameObject self, Component from)
        {
            return self.Position(from.Position());
        }

        /// <summary>把 from 的世界坐标写入自身。</summary>
        public static T SyncPositionFrom<T>(this T self, Component from) where T : Component
        {
            return self.Position(from.Position());
        }

        #endregion

        #region SyncPosition2DFrom

        /// <summary>把 from 的世界坐标 x/y 写入自身，保留自身 z。</summary>
        public static GameObject SyncPosition2DFrom(this GameObject self, GameObject from)
        {
            return self.Position2D(from.Position2D());
        }

        /// <summary>把 from 的世界坐标 x/y 写入自身，保留自身 z。</summary>
        public static T SyncPosition2DFrom<T>(this T self, GameObject from) where T : Component
        {
            return self.Position2D(from.Position2D());
        }

        /// <summary>把 from 的世界坐标 x/y 写入自身，保留自身 z。</summary>
        public static GameObject SyncPosition2DFrom(this GameObject self, Component from)
        {
            return self.Position2D(from.Position2D());
        }

        /// <summary>把 from 的世界坐标 x/y 写入自身，保留自身 z。</summary>
        public static T SyncPosition2DFrom<T>(this T self, Component from) where T : Component
        {
            return self.Position2D(from.Position2D());
        }

        #endregion

        #region SyncPositionTo

        /// <summary>把自身世界坐标写入目标 to，返回自身。</summary>
        public static GameObject SyncPositionTo(this GameObject self, GameObject to)
        {
            to.Position(self.Position());
            return self;
        }

        /// <summary>把自身世界坐标写入目标 to，返回自身。</summary>
        public static GameObject SyncPositionTo(this GameObject self, Component to)
        {
            to.Position(self.Position());
            return self;
        }

        /// <summary>把自身世界坐标写入目标 to，返回自身。</summary>
        public static T SyncPositionTo<T>(this T self, GameObject to) where T : Component
        {
            to.Position(self.Position());
            return self;
        }

        /// <summary>把自身世界坐标写入目标 to，返回自身。</summary>
        public static T SyncPositionTo<T>(this T self, Component to) where T : Component
        {
            to.Position(self.Position());
            return self;
        }

        #endregion

        #region SyncPosition2DTo

        /// <summary>把自身世界坐标 x/y 写入目标 to（保留 to 的 z），返回自身。</summary>
        public static GameObject SyncPosition2DTo(this GameObject self, GameObject to)
        {
            to.Position2D(self.Position2D());
            return self;
        }

        /// <summary>把自身世界坐标 x/y 写入目标 to（保留 to 的 z），返回自身。</summary>
        public static GameObject SyncPosition2DTo(this GameObject self, Component to)
        {
            to.Position2D(self.Position2D());
            return self;
        }

        /// <summary>把自身世界坐标 x/y 写入目标 to（保留 to 的 z），返回自身。</summary>
        public static T SyncPosition2DTo<T>(this T self, GameObject to) where T : Component
        {
            to.Position2D(self.Position2D());
            return self;
        }

        /// <summary>把自身世界坐标 x/y 写入目标 to（保留 to 的 z），返回自身。</summary>
        public static T SyncPosition2DTo<T>(this T self, Component to) where T : Component
        {
            to.Position2D(self.Position2D());
            return self;
        }

        #endregion
    }
}

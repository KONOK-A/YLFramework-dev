using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 方向、距离与 Vector2/Vector3 分量扩展。
    /// 所有距离方法基于 transform 坐标计算，不做 null 保护（强制查询，调用方保证对象有效）。
    /// 局部距离直接比较各自父空间中的坐标，不转换空间；父空间不同时不能视为实际空间距离。
    /// </summary>
    public static class VectorExtension
    {
        #region 计算方向

        /// <summary>从 self 指向 to 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionTo(this Component self, Component to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionTo(this GameObject self, GameObject to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionTo(this Component self, GameObject to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionTo(this GameObject self, Component to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 from 指向 self 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionFrom(this Component self, Component from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionFrom(this GameObject self, GameObject from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionFrom(this GameObject self, Component from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的世界坐标方向向量（未归一化）。</summary>
        public static Vector3 DirectionFrom(this Component self, GameObject from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 self 指向 to 的世界坐标单位方向；两点重合时返回零向量（Unity normalized 语义）。</summary>
        public static Vector3 NormalizedDirectionTo(this Component self, Component to) =>
            self.DirectionTo(to).normalized;
        /// <summary>从 self 指向 to 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionTo(this GameObject self, GameObject to) =>
            self.DirectionTo(to).normalized;
        /// <summary>从 self 指向 to 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionTo(this Component self, GameObject to) =>
            self.DirectionTo(to).normalized;
        /// <summary>从 self 指向 to 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionTo(this GameObject self, Component to) =>
            self.DirectionTo(to).normalized;
        /// <summary>从 from 指向 self 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionFrom(this Component self, Component from) =>
            self.DirectionFrom(from).normalized;
        /// <summary>从 from 指向 self 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionFrom(this GameObject self, GameObject from) =>
            self.DirectionFrom(from).normalized;
        /// <summary>从 from 指向 self 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionFrom(this GameObject self, Component from) =>
            self.DirectionFrom(from).normalized;
        /// <summary>从 from 指向 self 的世界坐标单位方向；两点重合时返回零向量。</summary>
        public static Vector3 NormalizedDirectionFrom(this Component self, GameObject from) =>
            self.DirectionFrom(from).normalized;
        /// <summary>从 self 指向 to 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DTo(this Component self, Component to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DTo(this GameObject self, GameObject to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DTo(this Component self, GameObject to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 self 指向 to 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DTo(this GameObject self, Component to) =>
            to.transform.position - self.transform.position;
        /// <summary>从 from 指向 self 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DFrom(this Component self, Component from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DFrom(this GameObject self, GameObject from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DFrom(this GameObject self, Component from) =>
            self.transform.position - from.transform.position;
        /// <summary>从 from 指向 self 的 2D 方向向量（仅 x/y，未归一化）。</summary>
        public static Vector2 Direction2DFrom(this Component self, GameObject from) =>
            self.transform.position - from.transform.position;

        /// <summary>从 self 指向 to 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DTo(this Component self, Component to) =>
            self.Direction2DTo(to).normalized;

        /// <summary>从 self 指向 to 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DTo(this GameObject self, GameObject to) =>
            self.Direction2DTo(to).normalized;

        /// <summary>从 self 指向 to 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DTo(this Component self, GameObject to) =>
            self.Direction2DTo(to).normalized;

        /// <summary>从 self 指向 to 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DTo(this GameObject self, Component to) =>
            self.Direction2DTo(to).normalized;

        /// <summary>从 from 指向 self 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DFrom(this Component self, Component from) =>
            self.Direction2DFrom(from).normalized;

        /// <summary>从 from 指向 self 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DFrom(this GameObject self, GameObject from) =>
            self.Direction2DFrom(from).normalized;

        /// <summary>从 from 指向 self 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DFrom(this GameObject self, Component from) =>
            self.Direction2DFrom(from).normalized;

        /// <summary>从 from 指向 self 的 2D 单位方向；两点重合时返回零向量。</summary>
        public static Vector2 NormalizedDirection2DFrom(this Component self, GameObject from) =>
            self.Direction2DFrom(from).normalized;


        #endregion

        #region Vector2、Vector3转换

        /// <summary>截取 Vector3 的 x/y 生成 Vector2。</summary>
        public static Vector2 ToVector2(this Vector3 self) => new Vector2(self.x, self.y);

        /// <summary>用 Vector2 的 x/y 与指定 z 生成 Vector3（z 默认 0）。</summary>
        public static Vector3 ToVector3(this Vector2 self, float z = 0)
        {
            return new Vector3(self.x, self.y, z);
        }

        #endregion

        #region 单一属性设置

        /// <summary>返回替换 x 分量后的新 Vector3（值类型，原值不变）。</summary>
        public static Vector3 X(this Vector3 self, float x)
        {
            self.x = x;
            return self;
        }
        /// <summary>返回替换 y 分量后的新 Vector3（值类型，原值不变）。</summary>
        public static Vector3 Y(this Vector3 self, float y)
        {
            self.y = y;
            return self;
        }
        /// <summary>返回替换 z 分量后的新 Vector3（值类型，原值不变）。</summary>
        public static Vector3 Z(this Vector3 self, float z)
        {
            self.z = z;
            return self;
        }
        /// <summary>返回替换 x 分量后的新 Vector2（值类型，原值不变）。</summary>
        public static Vector2 X(this Vector2 self, float x)
        {
            self.x = x;
            return self;
        }
        /// <summary>返回替换 y 分量后的新 Vector2（值类型，原值不变）。</summary>
        public static Vector2 Y(this Vector2 self, float y)
        {
            self.y = y;
            return self;
        }

        #endregion

        #region 计算距离

        /// <summary>两个对象世界坐标之间的 3D 距离。</summary>
        public static float Distance(this GameObject self, GameObject other)
        {
            return Vector3.Distance(self.Position(), other.Position());
        }
        /// <summary>两个对象世界坐标之间的 3D 距离。</summary>
        public static float Distance(this Component self, GameObject other)
        {
            return Vector3.Distance(self.Position(), other.Position());
        }
        /// <summary>两个对象世界坐标之间的 3D 距离。</summary>
        public static float Distance(this GameObject self, Component other)
        {
            return Vector3.Distance(self.Position(), other.Position());
        }
        /// <summary>两个对象世界坐标之间的 3D 距离。</summary>
        public static float Distance(this Component self, Component other)
        {
            return Vector3.Distance(self.Position(), other.Position());
        }
        /// <summary>两个对象世界坐标 x/y 之间的 2D 距离。</summary>
        public static float Distance2D(this GameObject self, GameObject other)
        {
            return Vector2.Distance(self.Position2D(), other.Position2D());
        }
        /// <summary>两个对象世界坐标 x/y 之间的 2D 距离。</summary>
        public static float Distance2D(this Component self, GameObject other)
        {
            return Vector2.Distance(self.Position2D(), other.Position2D());
        }
        /// <summary>两个对象世界坐标 x/y 之间的 2D 距离。</summary>
        public static float Distance2D(this GameObject self, Component other)
        {
            return Vector2.Distance(self.Position2D(), other.Position2D());
        }
        /// <summary>两个对象世界坐标 x/y 之间的 2D 距离。</summary>
        public static float Distance2D(this Component self, Component other)
        {
            return Vector2.Distance(self.Position2D(), other.Position2D());
        }
        /// <summary>两个对象局部坐标之间的 3D 距离。</summary>
        public static float LocalDistance(this GameObject self, GameObject other)
        {
            return Vector3.Distance(self.LocalPosition(), other.LocalPosition());
        }
        /// <summary>两个对象局部坐标之间的 3D 距离。</summary>
        public static float LocalDistance(this Component self, GameObject other)
        {
            return Vector3.Distance(self.LocalPosition(), other.LocalPosition());
        }
        /// <summary>两个对象局部坐标之间的 3D 距离。</summary>
        public static float LocalDistance(this GameObject self, Component other)
        {
            return Vector3.Distance(self.LocalPosition(), other.LocalPosition());
        }
        /// <summary>两个对象局部坐标之间的 3D 距离。</summary>
        public static float LocalDistance(this Component self, Component other)
        {
            return Vector3.Distance(self.LocalPosition(), other.LocalPosition());
        }
        /// <summary>两个对象局部坐标 x/y 之间的 2D 距离。</summary>
        public static float LocalDistance2D(this GameObject self, GameObject other)
        {
            return Vector2.Distance(self.LocalPosition2D(), other.LocalPosition2D());
        }
        /// <summary>两个对象局部坐标 x/y 之间的 2D 距离。</summary>
        public static float LocalDistance2D(this Component self, GameObject other)
        {
            return Vector2.Distance(self.LocalPosition2D(), other.LocalPosition2D());
        }
        /// <summary>两个对象局部坐标 x/y 之间的 2D 距离。</summary>
        public static float LocalDistance2D(this GameObject self, Component other)
        {
            return Vector2.Distance(self.LocalPosition2D(), other.LocalPosition2D());
        }
        /// <summary>两个对象局部坐标 x/y 之间的 2D 距离。</summary>
        public static float LocalDistance2D(this Component self, Component other)
        {
            return Vector2.Distance(self.LocalPosition2D(), other.LocalPosition2D());
        }

        #endregion
    }
}

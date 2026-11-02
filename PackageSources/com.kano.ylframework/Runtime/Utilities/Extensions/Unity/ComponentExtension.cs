using System;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Extensions
{
    /// <summary>
    /// Component 链式扩展，与 GameObjectExtension 对应。
    /// 变更类方法通常返回原组件；坐标查询会先访问自身的 transform，不保证 null 或已销毁组件可调用。
    /// 取得的 transform 无效时，查询按各方法说明返回默认值；显隐操作改变所在对象的 activeSelf，而非组件 enabled。
    /// 父子关系变更保留世界变换，新增组件可能触发 Unity 生命周期回调。
    /// 强制销毁类方法不做判空，Gracefully 版本按 Unity fake-null 跳过已销毁对象。
    /// </summary>
    public static class ComponentExtension
    {
        #region 初始化

        /// <summary>局部位置归零、局部旋转归为 identity、localScale 归一，返回原组件。</summary>
        public static T TransformLocalIdentity<T>(this T self) where T : Component
        {
            self.transform.localPosition = Vector3.zero;
            self.transform.localRotation = Quaternion.identity;
            self.transform.localScale = Vector3.one;
            return self;
        }

        /// <summary>世界位置归零、世界旋转归为 identity、localScale 归一，返回原组件。</summary>
        public static T TransformIdentity<T>(this T self) where T : Component
        {
            Transform transform = self.transform;
            if (transform != null)
            {
                transform.position = Vector3.zero;
                transform.rotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            }
            return self as T;
        }

        #endregion

        #region 位置设置
        /// <summary>设置世界坐标，返回原组件。</summary>
        public static T Position<T>(this T self, Vector3 position) where T : Component
        {
            Transform t = self.transform;
            if (t != null)
            {
                t.position = position;
            }

            return self as T;
        }
        /// <summary>获取世界坐标；transform 无效时返回 <see cref="Vector3.zero"/>。</summary>
        public static Vector3 Position(this Component self)
        {
            var t = self.transform;
            return t != null ? t.position : Vector3.zero;
        }
        /// <summary>设置世界坐标，返回原组件。</summary>
        public static T Position<T>(this T selfComponent, float x, float y, float z) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.position = new Vector3(x, y, z);
            }
            return selfComponent;
        }
        /// <summary>获取世界坐标 x/y（忽略 z）；transform 无效时返回 <see cref="Vector2.zero"/>。</summary>
        public static Vector2 Position2D(this Component self)
        {
            var t = self.transform;
            return t != null ? new Vector2(self.PositionX(), self.PositionY()) : Vector2.zero;
        }
        /// <summary>设置世界坐标 x/y（保留 z），返回原组件。</summary>
        public static T Position2D<T>(this T self, Vector2 position) where T : Component
        {
            if (self.transform != null)
            {
                var currentPosition = self.transform.position;
                currentPosition.x = position.x;
                currentPosition.y = position.y;
                self.transform.position = currentPosition;
            }
            return self;
        }
        /// <summary>设置世界坐标 x/y（保留 z），返回原组件。</summary>
        public static T Position2D<T>(this T self, float x, float y) where T : Component
        {
            Transform t = self.transform;
            if (t != null)
            {
                var currentPosition = t.position;
                currentPosition.x = x;
                currentPosition.y = y;
                t.position = currentPosition;
            }
            return self;
        }
        /// <summary>设置世界坐标 x（保留 y/z），返回原组件。</summary>
        public static T PositionX<T>(this T self, float x) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var position = t.position;
                position.x = x;
                t.position = position;
            }

            return self as T;
        }
        /// <summary>以 getter 计算结果设置世界坐标 x，返回原组件。</summary>
        public static T PositionX<T>(this T selfComponent, Func<float, float> getter) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var position = selfComponent.transform.position;
                position.x = getter(position.x);
                selfComponent.transform.position = position;
            }
            return selfComponent;
        }
        /// <summary>获取世界坐标 x；transform 无效时返回 0。</summary>
        public static float PositionX(this Component self)
        {
            var t = self.transform;
            return t != null ? t.position.x : 0;
        }
        /// <summary>设置世界坐标 y（保留 x/z），返回原组件。</summary>
        public static T PositionY<T>(this T self, float y) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var position = t.position;
                position.y = y;
                t.position = position;
            }
            return self as T;
        }
        /// <summary>以 getter 计算结果设置世界坐标 y，返回原组件。</summary>
        public static T PositionY<T>(this T selfComponent, Func<float, float> getter) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var position = selfComponent.transform.position;
                position.y = getter(position.y);
                selfComponent.transform.position = position;
            }
            return selfComponent;
        }
        /// <summary>获取世界坐标 y；transform 无效时返回 0。</summary>
        public static float PositionY(this Component self)
        {
            var t = self.transform;
            return t != null ? t.position.y : 0;
        }
        /// <summary>设置世界坐标 z（保留 x/y），返回原组件。</summary>
        public static T PositionZ<T>(this T self, float z) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var position = t.position;
                position.z = z;
                t.position = position;
            }
            return self as T;
        }
        /// <summary>以 getter 计算结果设置世界坐标 z，返回原组件。</summary>
        public static T PositionZ<T>(this T self, Func<float, float> getter) where T : Component
        {
            if (self != null)
            {
                var position = self.transform.position;
                position.z = getter(position.z);
                self.transform.position = position;
            }
            return self;
        }
        /// <summary>获取世界坐标 z；transform 无效时返回 0。</summary>
        public static float PositionZ(this Component self)
        {
            var t = self.transform;
            return t != null ? t.position.z : 0;
        }
        /// <summary>设置局部坐标，返回原组件。</summary>
        public static T LocalPosition<T>(this T self, Vector3 localPosition) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                t.localPosition = localPosition;
            }
            return self as T;
        }
        /// <summary>设置局部坐标，返回原组件。</summary>
        public static T LocalPosition<T>(this T selfComponent, float x, float y, float z) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.localPosition = new Vector3(x, y, z);
            }
            return selfComponent;
        }
        /// <summary>获取局部坐标；transform 无效时返回 <see cref="Vector3.zero"/>。</summary>
        public static Vector3 LocalPosition(this Component self)
        {
            var t = self.transform;
            return t != null ? t.localPosition : Vector3.zero;
        }
        /// <summary>获取局部坐标 x/y（忽略 z）；transform 无效时返回 <see cref="Vector2.zero"/>。</summary>
        public static Vector2 LocalPosition2D(this Component self)
        {
            Transform t = self.transform;
            return t != null ? new Vector2(t.localPosition.x, t.localPosition.y) : Vector2.zero;
        }
        /// <summary>设置局部坐标 x/y（保留 z），返回原组件。</summary>
        public static T LocalPosition2D<T>(this T self, float x, float y) where T : Component
        {
            Transform t = self.transform;
            if (t != null)
            {
                var currentLocalPosition = t.localPosition;
                currentLocalPosition.x = x;
                currentLocalPosition.y = y;
                t.localPosition = currentLocalPosition;
            }
            return self;
        }
        /// <summary>设置局部坐标 x（保留 y/z），返回原组件。</summary>
        public static T LocalPositionX<T>(this T self, float x) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var localPosition = t.localPosition;
                localPosition.x = x;
                t.localPosition = localPosition;
            }
            return self as T;
        }
        /// <summary>获取局部坐标 x；transform 无效时返回 0。</summary>
        public static float LocalPositionX(this Component self)
        {
            var t = self.transform;
            return t != null ? t.localPosition.x : 0;
        }
        /// <summary>设置局部坐标 y（保留 x/z），返回原组件。</summary>
        public static T LocalPositionY<T>(this T self, float y) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var localPosition = t.localPosition;
                localPosition.y = y;
                t.localPosition = localPosition;
            }
            return self as T;
        }
        /// <summary>获取局部坐标 y；transform 无效时返回 0。</summary>
        public static float LocalPositionY(this Component self)
        {
            var t = self.transform;
            return t != null ? t.localPosition.y : 0;
        }
        /// <summary>设置局部坐标 z（保留 x/y），返回原组件。</summary>
        public static T LocalPositionZ<T>(this T self, float z) where T : Component
        {
            var t = self.transform;
            if (t != null)
            {
                var localPosition = t.localPosition;
                localPosition.z = z;
                t.localPosition = localPosition;
            }
            return self as T;
        }
        /// <summary>获取局部坐标 z；transform 无效时返回 0。</summary>
        public static float LocalPositionZ(this Component self)
        {
            var t = self.transform;
            return t != null ? t.localPosition.z : 0;
        }


        #endregion

        #region 旋转设置

        /// <summary>获取世界旋转；transform 无效时返回 identity。</summary>
        public static Quaternion Rotation<T>(this T selfComponent) where T : Component
        {
            return selfComponent.transform != null ? selfComponent.transform.rotation : Quaternion.identity;
        }
        /// <summary>设置世界旋转，返回原组件。</summary>
        public static T Rotation<T>(this T selfComponent, Quaternion rotation) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.rotation = rotation;
            }
            return selfComponent;
        }
        /// <summary>获取局部旋转；transform 无效时返回 identity。</summary>
        public static Quaternion LocalRotation<T>(this T selfComponent) where T : Component
        {
            return selfComponent.transform != null ? selfComponent.transform.localRotation : Quaternion.identity;
        }
        /// <summary>设置局部旋转，返回原组件。</summary>
        public static T LocalRotation<T>(this T selfComponent, Quaternion localRotation) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.localRotation = localRotation;
            }
            return selfComponent;
        }

        /// <summary>获取世界欧拉角；transform 无效时返回 <see cref="Vector3.zero"/>。</summary>
        public static Vector3 EulerAngles(this Component self)
        {
            return self.transform != null ? self.transform.eulerAngles : Vector3.zero;
        }
        /// <summary>设置世界欧拉角，返回原组件。</summary>
        public static T EulerAngles<T>(this T self, Vector3 eulerAngles) where T : Component
        {
            if (self.transform != null)
            {
                self.transform.eulerAngles = eulerAngles;
            }
            return self;
        }
        /// <summary>只设置世界欧拉角 x（保留 y/z），返回原组件。</summary>
        public static T EulerAnglesX<T>(this T self, float x) where T : Component
        {
            self.EulerAngles(self.EulerAngles().X(x));
            return self;
        }

        /// <summary>获取世界欧拉角 x；transform 无效时返回 0。</summary>
        public static float EulerAnglesX(this Component self)
        {
            var t = self.transform;
            return t != null ? t.eulerAngles.x : 0;
        }


        /// <summary>只设置世界欧拉角 y（保留 x/z），返回原组件。</summary>
        public static T EulerAnglesY<T>(this T self, float y) where T : Component
        {
            self.EulerAngles(self.EulerAngles().Y(y));
            return self;
        }

        /// <summary>获取世界欧拉角 y；transform 无效时返回 0。</summary>
        public static float EulerAnglesY(this Component self)
        {
            var t = self.transform;
            return t != null ? t.eulerAngles.y : 0;
        }

        /// <summary>只设置世界欧拉角 z（保留 x/y），返回原组件。</summary>
        public static T EulerAnglesZ<T>(this T self, float z) where T : Component
        {
            self.EulerAngles(self.EulerAngles().Z(z));
            return self;
        }

        /// <summary>获取世界欧拉角 z；transform 无效时返回 0。</summary>
        public static float EulerAnglesZ(this Component self)
        {
            var t = self.transform;
            return t != null ? t.eulerAngles.z : 0;
        }

        /// <summary>获取局部欧拉角；transform 无效时返回 <see cref="Vector3.zero"/>。</summary>
        public static Vector3 LocalEulerAngles<T>(this T selfComponent) where T : Component
        {
            return selfComponent.transform != null ? selfComponent.transform.localEulerAngles : Vector3.zero;
        }
        /// <summary>设置局部欧拉角，返回原组件。</summary>
        public static T LocalEulerAngles<T>(this T self, Vector3 localEulerAngles) where T : Component
        {
            if (self.transform != null)
            {
                self.transform.localEulerAngles = localEulerAngles;
            }
            return self;
        }

        /// <summary>只设置局部欧拉角 z（保留 x/y），返回原组件。</summary>
        public static T LocalEulerAnglesZ<T>(this T self, float z) where T : Component
        {
            self.LocalEulerAngles(self.LocalEulerAngles().Z(z));
            return self;
        }

        /// <summary>获取局部欧拉角 z；transform 无效时返回 0。</summary>
        public static float LocalEulerAnglesZ(this Component self)
        {
            var t = self.transform;
            return t != null ? t.localEulerAngles.z : 0;
        }

        #endregion

        #region 缩放设置
        //注意：lossyScale只有Getter
        /// <summary>获取 lossyScale；transform 无效时返回 <see cref="Vector3.one"/>（只读）。</summary>
        public static Vector3 Scale<T>(this T selfComponent) where T : Component
        {
            return selfComponent.transform != null ? selfComponent.transform.lossyScale : Vector3.one;
        }
        /// <summary>设置 localScale，返回原组件。</summary>
        public static T LocalScale<T>(this T selfComponent, Vector3 scale) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.localScale = scale;
            }
            return selfComponent;
        }
        /// <summary>获取 localScale；transform 无效时返回 <see cref="Vector3.zero"/>。</summary>
        public static Vector3 LocalScale<T>(this T selfComponent) where T : Component
        {
            return selfComponent.transform != null ? selfComponent.transform.localScale : Vector3.zero;
        }
        /// <summary>整体设置 localScale，返回原组件。</summary>
        public static T LocalScale<T>(this T selfComponent, float xyz) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.localScale = Vector3.one * xyz;
            }
            return selfComponent;
        }
        /// <summary>设置 localScale 的 x/y/z，返回原组件。</summary>
        public static T LocalScale<T>(this T selfComponent, float x, float y, float z) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var localScale = selfComponent.transform.localScale;
                localScale.x = x;
                localScale.y = y;
                localScale.z = z;
                selfComponent.transform.localScale = localScale;
            }
            return selfComponent;
        }
        /// <summary>设置 localScale 的 x/y（保留 z），返回原组件。</summary>
        public static T LocalScale2D<T>(this T selfComponent, float x, float y) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var localScale = selfComponent.transform.localScale;
                localScale.x = x;
                localScale.y = y;
                selfComponent.transform.localScale = localScale;
            }
            return selfComponent;
        }
        /// <summary>设置 localScale.x（保留 y/z），返回原组件。</summary>
        public static T LocalScaleX<T>(this T selfComponent, float x) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var localScale = selfComponent.transform.localScale;
                localScale.x = x;
                selfComponent.transform.localScale = localScale;
            }
            return selfComponent;
        }
        /// <summary>获取 localScale.x；transform 无效时返回 0。</summary>
        public static float LocalScaleX<T>(this T self) where T : Component
        {
            return self.transform != null ? self.transform.localScale.x : 0;
        }
        /// <summary>设置 localScale.y（保留 x/z），返回原组件。</summary>
        public static T LocalScaleY<T>(this T self, float y) where T : Component
        {
            if (self.transform != null)
            {
                var localScale = self.transform.localScale;
                localScale.y = y;
                self.transform.localScale = localScale;
            }
            return self;
        }
        /// <summary>获取 localScale.y；transform 无效时返回 0。</summary>
        public static float LocalScaleY<T>(this T self) where T : Component
        {
            return self.transform != null ? self.transform.localScale.y : 0;
        }
        /// <summary>设置 localScale.z（保留 x/y），返回原组件。</summary>
        public static T LocalScaleZ<T>(this T selfComponent, float z) where T : Component
        {
            if (selfComponent.transform != null)
            {
                var localScale = selfComponent.transform.localScale;
                localScale.z = z;
                selfComponent.transform.localScale = localScale;
            }
            return selfComponent;
        }
        /// <summary>获取 localScale.z；transform 无效时返回 0。</summary>
        public static float LocalScaleZ<T>(this T self) where T : Component
        {
            return self.transform != null ? self.transform.localScale.z : 0;
        }

        #endregion

        #region 显示、隐藏与销毁

        /// <summary>激活所在 GameObject，返回原组件。</summary>
        public static T Show<T>(this T selfComponent) where T : Component
        {
            selfComponent.gameObject.Show();
            return selfComponent;
        }
        /// <summary>失活所在 GameObject，返回原组件。</summary>
        public static T Hide<T>(this T selfComponent) where T : Component
        {
            selfComponent.gameObject.Hide();
            return selfComponent;
        }

        /// <summary>销毁所在 GameObject（延迟到帧末生效）；不做判空，属于强制操作。</summary>
        public static void DestroyGameObj<T>(this T selfBehaviour) where T : Component
        {
            selfBehaviour.gameObject.DestroySelf();
        }

        /// <summary>销毁所在 GameObject；组件或对象为 null / 已销毁时跳过。副作用：Destroy。</summary>
        public static void DestroyGameObjGracefully<T>(this T selfBehaviour) where T : Component
        {
            if (selfBehaviour && selfBehaviour.gameObject)
            {
                selfBehaviour.gameObject.DestroySelfGracefully();
            }
        }

        /// <summary>延迟 delay 个受 Time.timeScale 影响的秒后销毁所在 GameObject，返回原组件。</summary>
        public static T DestroyGameObjectWithDelay<T>(this T selfBehaviour, float delay) where T : Component
        {
            selfBehaviour.gameObject.DestroySelfWithDelay(delay);
            return selfBehaviour;
        }

        /// <summary>按受 Time.timeScale 影响的 delay 秒延迟销毁所在 GameObject；组件或对象无效时跳过，返回原组件。</summary>
        public static T DestroyGameObjectWithDelayGracefully<T>(this T selfBehaviour, float delay) where T : Component
        {
            if (selfBehaviour && selfBehaviour.gameObject)
            {
                selfBehaviour.gameObject.DestroySelfWithDelay(delay);
            }

            return selfBehaviour;
        }

        #endregion

        #region 层级、标签

        /// <summary>设置所在 GameObject 的 layer，返回原组件。</summary>
        public static T Layer<T>(this T selfComponent, int layer) where T : Component
        {
            selfComponent.gameObject.layer = layer;
            return selfComponent;
        }

        /// <summary>设置所在 GameObject 的层名对应 layer；层名未定义时会照旧写入 -1（已知限制）。</summary>
        public static T Layer<T>(this T selfComponent, string layerName) where T : Component
        {
            selfComponent.gameObject.layer = LayerMask.NameToLayer(layerName);
            return selfComponent;
        }


        /// <summary>判断所在层是否包含在 layerMask 中；null 或已销毁组件返回 false。</summary>
        public static bool IsInLayerMask<T>(this T selfComponent, LayerMask layerMask) where T : Component
        {
            return selfComponent != null && LayerMaskUtility.IsInLayerMask(selfComponent.gameObject, layerMask);
        }

        /// <summary>设置所在 GameObject 的 tag，返回原组件。</summary>
        public static T Tag<T>(this T selfComponent, string tag) where T : Component
        {
            selfComponent.gameObject.tag = tag;
            return selfComponent;
        }

        #endregion

        #region 组件获取和添加

        /// <summary>
        /// 获取所在 GameObject 上的指定组件；不存在（含组件已销毁的 fake-null）时自动添加。
        /// 副作用：可能执行 AddComponent。
        /// </summary>
        public static T GetOrAddComponent<T>(this Component component) where T : Component
        {
            return component.gameObject.GetOrAddComponent<T>();
        }

        #endregion

        #region 子物体设置

        /// <summary>销毁所有直接子物体（延迟到帧末生效），返回原组件。</summary>
        public static T DestroyChildren<T>(this T selfComponent) where T : Component
        {
            if (selfComponent.transform == null || selfComponent.transform.childCount == 0)
            {
                return selfComponent;
            }

            var childCount = selfComponent.transform.childCount;
            for (var i = childCount - 1; i >= 0; i--)
            {
                selfComponent.transform.GetChild(i).DestroyGameObjGracefully();
            }
            return selfComponent;
        }

        /// <summary>销毁满足条件的直接子物体（延迟到帧末生效），返回原组件。</summary>
        public static T DestroyChildrenWithCondition<T>(this T selfComponent, Func<Transform, bool> condition)
            where T : Component
        {
            if (selfComponent.transform == null || selfComponent.transform.childCount == 0)
            {
                return selfComponent;
            }

            var childCount = selfComponent.transform.childCount;
            for (var i = childCount - 1; i >= 0; i--)
            {
                var child = selfComponent.transform.GetChild(i);
                if (condition(child))
                {
                    child.DestroyGameObjGracefully();
                }
            }
            return selfComponent;
        }
        /// <summary>把自身所在对象移到同级末尾，返回原组件。</summary>
        public static T AsLastSibling<T>(this T selfComponent) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.SetAsLastSibling();
            }
            return selfComponent;
        }

        /// <summary>把自身所在对象移到同级开头，返回原组件。</summary>
        public static T AsFirstSibling<T>(this T selfComponent) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.SetAsFirstSibling();
            }
            return selfComponent;
        }

        /// <summary>把自身所在对象移到指定同级索引，返回原组件。</summary>
        public static T AsIndexSibling<T>(this T selfComponent, int index) where T : Component
        {
            if (selfComponent.transform != null)
            {
                selfComponent.transform.SetSiblingIndex(index);
            }
            return selfComponent;
        }

        /// <summary>把 child 挂到自身所在对象下，返回原组件。</summary>
        public static T AddChild<T>(this T self, Transform child) where T : Component
        {
            if (self.transform != null)
            {
                child.parent = self.transform;
            }
            return self;
        }

        /// <summary>把 child 挂到自身所在对象下，返回原组件。</summary>
        public static T AddChild<T>(this T self, GameObject child) where T : Component
        {
            if (self.transform != null)
            {
                child.transform.parent = self.transform;
            }
            return self;
        }

        /// <summary>把 child 挂到自身所在对象下，并按 isLastOrFirstSibling 决定移到末尾或开头，返回原组件。</summary>
        public static T AddChild<T>(this T self, Transform child, bool isLastOrFirstSibling) where T : Component
        {
            if (self.transform != null)
            {
                child.parent = self.transform;
                if (isLastOrFirstSibling)
                {
                    child.SetAsLastSibling();
                }
                else
                {
                    child.SetAsFirstSibling();
                }
            }
            return self;
        }

        /// <summary>把 child 挂到自身所在对象下并设置同级索引，返回原组件。</summary>
        public static T AddChild<T>(this T self, Transform child, int index) where T : Component
        {
            if (self.transform != null)
            {
                child.parent = self.transform;
                child.SetSiblingIndex(index);
            }
            return self;
        }

        #endregion

    }
}

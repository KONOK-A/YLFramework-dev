using System;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Extensions
{
    /// <summary>
    /// GameObject 链式扩展。变更类方法返回原对象以便串联；查询方法不修改对象。
    /// 判空使用 Unity 的 <c>== null</c> 语义，但多数方法不保护无效的自身对象。
    /// 显隐只设置 activeSelf，实际激活仍受祖先状态影响；父子关系变更保留世界变换。
    /// </summary>
    public static class GameObjectExtension
    {
        #region 显示与隐藏
        /// <summary>激活 GameObject，返回原对象。</summary>
        public static GameObject Show(this GameObject selfObj)
        {
            selfObj.SetActive(true);
            return selfObj;
        }

        /// <summary>失活 GameObject，返回原对象。</summary>
        public static GameObject Hide(this GameObject selfObj)
        {
            selfObj.SetActive(false);
            return selfObj;
        }
        #endregion

        #region 层级、标签、父物体

        /// <summary>设置自身及活跃子孙的 layer；保留默认 GetComponentsInChildren 的 inactive 排除语义。</summary>
        public static void SetLayer(this GameObject gameObject, int layer)
        {
            Transform[] transforms = gameObject.GetComponentsInChildren<Transform>();
            foreach (var item in transforms)
            {
                item.gameObject.layer = layer;
            }
        }

        /// <summary>按层名设置自身及活跃子孙的 layer；未定义层名沿用 Unity 的 -1 写入行为。</summary>
        public static void SetLayer(this GameObject gameObject, string layerName)
        {
            SetLayer(gameObject, LayerMask.NameToLayer(layerName));
        }

        /// <summary>设置 layer，返回原对象。</summary>
        public static GameObject Layer(this GameObject selfObj, int layer)
        {
            selfObj.layer = layer;
            return selfObj;
        }
        /// <summary>设置 layer 名对应的层；层名未定义时 <see cref="LayerMask.NameToLayer"/> 返回 -1，会照旧写入（已知限制）。</summary>
        public static GameObject Layer(this GameObject selfObj, string layerName)
        {
            selfObj.layer = LayerMask.NameToLayer(layerName);
            return selfObj;
        }
        /// <summary>设置 tag，返回原对象。</summary>
        public static GameObject Tag(this GameObject selfObj, string tag)
        {
            selfObj.tag = tag;
            return selfObj;
        }
        /// <summary>判断所在层是否包含在 layerMask 中；null 或已销毁对象返回 false。</summary>
        public static bool IsInLayerMask(this GameObject selfObj, LayerMask layerMask)
        {
            return LayerMaskUtility.IsInLayerMask(selfObj, layerMask);
        }
        /// <summary>设置父物体；parent 为 null 或已销毁时挂到场景根（SetParent(null)，保留世界变换），返回原对象。</summary>
        public static T Parent<T>(this T self, Component parent) where T : Component
        {
            self.transform.SetParent(parent == null ? null : parent.transform);
            return self;
        }

        /// <summary>获取父 Transform；无父物体时返回 null。</summary>
        public static Transform Parent<T>(this T self) where T : Component
        {
            return self.transform.parent;
        }

        /// <summary>设置父物体；parent 为 null 或已销毁时挂到场景根，返回原对象。</summary>
        public static GameObject Parent(this GameObject self, Component parent)
        {
            self.transform.SetParent(parent == null ? null : parent.transform);
            return self;
        }

        /// <summary>设置父物体；parent 为 null 或已销毁时挂到场景根，返回原对象。</summary>
        public static T Parent<T>(this T self, GameObject parent) where T : Component
        {
            self.transform.SetParent(parent == null ? null : parent.transform);
            return self;
        }

        /// <summary>设置父物体；parent 为 null 或已销毁时挂到场景根，返回原对象。</summary>
        public static GameObject Parent(this GameObject self, GameObject parent)
        {
            self.transform.SetParent(parent == null ? null : parent.transform);
            return self;
        }

        #endregion

        #region 初始化

        /// <summary>世界位置归零、世界旋转归为 identity、localScale 归一，返回原对象。</summary>
        public static GameObject Identity(this GameObject self)
        {
            self.transform.position = Vector3.zero;
            self.transform.rotation = Quaternion.identity;
            self.transform.localScale = Vector3.one;
            return self;
        }
        /// <summary>局部位置归零、局部旋转归为 identity、localScale 归一，返回原对象。</summary>
        public static GameObject LocalIdentity(this GameObject self)
        {
            self.transform.localPosition = Vector3.zero;
            self.transform.localRotation = Quaternion.identity;
            self.transform.localScale = Vector3.one;
            return self;
        }

        #endregion

        #region 位置设置

        /// <summary>获取世界坐标。</summary>
        public static Vector3 Position(this GameObject self)
        {
            return self.transform.position;
        }
        /// <summary>设置世界坐标，返回原对象。</summary>
        public static GameObject Position(this GameObject self, Vector3 position)
        {
            self.transform.position = position;
            return self;
        }
        /// <summary>设置世界坐标，返回原对象。</summary>
        public static GameObject Position(this GameObject self, float x, float y, float z)
        {
            self.transform.position = new Vector3(x, y, z);
            return self;
        }
        /// <summary>获取世界坐标的 x/y（忽略 z）。</summary>
        public static Vector2 Position2D(this GameObject selfObj)
        {
            return selfObj.transform.position;
        }
        /// <summary>设置世界坐标的 x/y（保留 z），返回原对象。</summary>
        public static GameObject Position2D(this GameObject self, Vector2 position)
        {
            var currentPosition = self.transform.position;
            currentPosition.x = position.x;
            currentPosition.y = position.y;
            self.transform.position = currentPosition;
            return self;
        }
        /// <summary>设置世界坐标的 x/y（保留 z），返回原对象。</summary>
        public static GameObject Position2D(this GameObject self, float x, float y)
        {
            var position = self.transform.position;
            position.x = x;
            position.y = y;
            self.transform.position = position;
            return self;
        }
        /// <summary>获取世界坐标 x。</summary>
        public static float PositionX(this GameObject self)
        {
            return self.transform.position.x;
        }
        /// <summary>设置世界坐标 x（保留 y/z），返回原对象。</summary>
        public static GameObject PositionX(this GameObject self, float x)
        {
            var position = self.transform.position;
            position.x = x;
            self.transform.position = position;
            return self;
        }
        /// <summary>以 getter 计算结果设置世界坐标 x，返回原对象。</summary>
        public static GameObject PositionX(this GameObject self, Func<float, float> getter)
        {
            var position = self.transform.position;
            position.x = getter(position.x);
            self.transform.position = position;
            return self;
        }
        /// <summary>获取世界坐标 y。</summary>
        public static float PositionY(this GameObject self)
        {
            return self.transform.position.y;
        }
        /// <summary>设置世界坐标 y（保留 x/z），返回原对象。</summary>
        public static GameObject PositionY(this GameObject self, float y)
        {
            var position = self.transform.position;
            position.y = y;
            self.transform.position = position;
            return self;
        }
        /// <summary>以 getter 计算结果设置世界坐标 y，返回原对象。</summary>
        public static GameObject PositionY(this GameObject self, Func<float, float> gettter)
        {
            var position = self.transform.position;
            position.y = gettter(position.y);
            self.transform.position = position;
            return self;
        }
        /// <summary>获取世界坐标 z。</summary>
        public static float PositionZ(this GameObject self)
        {
            return self.transform.position.z;
        }
        /// <summary>设置世界坐标 z（保留 x/y），返回原对象。</summary>
        public static GameObject PositionZ(this GameObject self, float z)
        {
            var position = self.transform.position;
            position.z = z;
            self.transform.position = position;
            return self;
        }
        /// <summary>以 getter 计算结果设置世界坐标 z，返回原对象。</summary>
        public static GameObject PositionZ(this GameObject self, Func<float, float> getter)
        {
            var position = self.transform.position;
            position.z = getter(position.z);
            self.transform.position = position;
            return self;
        }
        /// <summary>获取局部坐标。</summary>
        public static Vector3 LocalPosition(this GameObject selfObj)
        {
            return selfObj.transform.localPosition;
        }
        /// <summary>设置局部坐标，返回原对象。</summary>
        public static GameObject LocalPosition(this GameObject self, Vector3 localPos)
        {
            self.transform.localPosition = localPos;
            return self;
        }
        /// <summary>设置局部坐标，返回原对象。</summary>
        public static GameObject LocalPosition(this GameObject self, float x, float y, float z)
        {
            self.transform.localPosition = new Vector3(x, y, z);
            return self;
        }
        /// <summary>获取局部坐标的 x/y（忽略 z）。</summary>
        public static Vector2 LocalPosition2D(this GameObject selfObj)
        {
            var localPosition = selfObj.transform.localPosition;
            return new Vector2(localPosition.x, localPosition.y);
        }
        /// <summary>设置局部坐标的 x/y（保留 z），返回原对象。</summary>
        public static GameObject LocalPosition2D(this GameObject self, Vector2 localPos)
        {
            var currentLocalPosition = self.transform.localPosition;
            currentLocalPosition.x = localPos.x;
            currentLocalPosition.y = localPos.y;
            self.transform.localPosition = currentLocalPosition;
            return self;
        }
        /// <summary>设置局部坐标的 x/y（保留 z），返回原对象。</summary>
        public static GameObject LocalPosition2D(this GameObject self, float x, float y)
        {
            var localPosition = self.transform.localPosition;
            localPosition.x = x;
            localPosition.y = y;
            self.transform.localPosition = localPosition;
            return self;
        }
        /// <summary>获取局部坐标 x。</summary>
        public static float LocalPositionX(this GameObject selfObj)
        {
            return selfObj.transform.localPosition.x;
        }
        /// <summary>设置局部坐标 x（保留 y/z），返回原对象。</summary>
        public static GameObject LocalPositionX(this GameObject self, float x)
        {
            var localPosition = self.transform.localPosition;
            localPosition.x = x;
            self.transform.localPosition = localPosition;
            return self;
        }
        /// <summary>获取局部坐标 y。</summary>
        public static float LocalPositionY(this GameObject selfObj)
        {
            return selfObj.transform.localPosition.y;
        }
        /// <summary>设置局部坐标 y（保留 x/z），返回原对象。</summary>
        public static GameObject LocalPositionY(this GameObject selfComponent, float y)
        {
            var localPosition = selfComponent.transform.localPosition;
            localPosition.y = y;
            selfComponent.transform.localPosition = localPosition;
            return selfComponent;
        }
        /// <summary>获取局部坐标 z。</summary>
        public static float LocalPositionZ(this GameObject selfObj)
        {
            return selfObj.transform.localPosition.z;
        }
        /// <summary>设置局部坐标 z（保留 x/y），返回原对象。</summary>
        public static GameObject LocalPositionZ(this GameObject self, float z)
        {
            var localPosition = self.transform.localPosition;
            localPosition.z = z;
            self.transform.localPosition = localPosition;
            return self;
        }

        #endregion

        #region 旋转设置

        /// <summary>设置世界旋转，返回原对象。</summary>
        public static GameObject Rotation(this GameObject self, Quaternion rotation)
        {
            self.transform.rotation = rotation;
            return self;
        }
        /// <summary>获取世界旋转。</summary>
        public static Quaternion Rotation(this GameObject self)
        {
            return self.transform.rotation;
        }
        /// <summary>获取局部旋转。</summary>
        public static Quaternion LocalRotation(this GameObject self)
        {
            return self.transform.localRotation;
        }
        /// <summary>设置局部旋转，返回原对象。</summary>
        public static GameObject LocalRotation(this GameObject selfComponent, Quaternion localRotation)
        {
            selfComponent.transform.localRotation = localRotation;
            return selfComponent;
        }
        /// <summary>获取局部欧拉角。</summary>
        public static Vector3 LocalEulerAngles(this GameObject self) => self.transform.localEulerAngles;
        /// <summary>设置局部欧拉角，返回原对象。</summary>
        public static GameObject LocalEulerAngles(this GameObject self, Vector3 localEulerAngles)
        {
            self.transform.localEulerAngles = localEulerAngles;
            return self;
        }
        /// <summary>只设置局部欧拉角 z（保留 x/y），返回原对象。</summary>
        public static GameObject LocalEulerAnglesZ(this GameObject self, float z)
        {
            self.LocalEulerAngles(self.LocalEulerAngles().Z(z));
            return self;
        }
        /// <summary>获取世界欧拉角。</summary>
        public static Vector3 EulerAngles(this GameObject self) => self.transform.eulerAngles;
        /// <summary>设置世界欧拉角，返回原对象。</summary>
        public static GameObject EulerAngles(this GameObject self, Vector3 eulerAngles)
        {
            self.transform.eulerAngles = eulerAngles;
            return self;
        }
        /// <summary>只设置世界欧拉角 z（保留 x/y），返回原对象。</summary>
        public static GameObject EulerAnglesZ(this GameObject self, float z)
        {
            self.EulerAngles(self.EulerAngles().Z(z));
            return self;
        }

        #endregion

        #region 缩放设置

        /// <summary>获取 lossyScale（只读，无法直接设置）。</summary>
        public static Vector3 Scale(this GameObject selfComponent)
        {
            return selfComponent.transform.lossyScale;
        }
        /// <summary>设置 localScale，返回原对象。</summary>
        public static GameObject LocalScale(this GameObject self, Vector3 scale)
        {
            self.transform.localScale = scale;
            return self;
        }
        /// <summary>获取 localScale。</summary>
        public static Vector3 LocalScale(this GameObject self)
        {
            return self.transform.localScale;
        }
        /// <summary>整体设置 localScale，返回原对象。</summary>
        public static GameObject LocalScale(this GameObject self, float xyz)
        {
            self.transform.localScale = Vector3.one * xyz;
            return self;
        }
        /// <summary>设置 localScale 的 x/y/z，返回原对象。</summary>
        public static GameObject LocalScale(this GameObject selfComponent, float x, float y, float z)
        {
            var localScale = selfComponent.transform.localScale;
            localScale.x = x;
            localScale.y = y;
            localScale.z = z;
            selfComponent.transform.localScale = localScale;
            return selfComponent;
        }
        /// <summary>设置 localScale 的 x/y（保留 z），返回原对象。</summary>
        public static GameObject LocalScale(this GameObject selfComponent, float x, float y)
        {
            var localScale = selfComponent.transform.localScale;
            localScale.x = x;
            localScale.y = y;
            selfComponent.transform.localScale = localScale;
            return selfComponent;
        }
        /// <summary>设置 localScale.x（保留 y/z），返回原对象。</summary>
        public static GameObject LocalScaleX(this GameObject self, float x)
        {
            var localScale = self.transform.localScale;
            localScale.x = x;
            self.transform.localScale = localScale;
            return self;
        }
        /// <summary>获取 localScale.x。</summary>
        public static float LocalScaleX(this GameObject self)
        {
            return self.transform.localScale.x;
        }
        /// <summary>设置 localScale.y（保留 x/z），返回原对象。</summary>
        public static GameObject LocalScaleY(this GameObject self, float y)
        {
            var localScale = self.transform.localScale;
            localScale.y = y;
            self.transform.localScale = localScale;
            return self;
        }
        /// <summary>获取 localScale.y。</summary>
        public static float LocalScaleY(this GameObject self)
        {
            return self.transform.localScale.y;
        }
        /// <summary>设置 localScale.z（保留 x/y），返回原对象。</summary>
        public static GameObject LocalScaleZ(this GameObject selfComponent, float z)
        {
            var localScale = selfComponent.transform.localScale;
            localScale.z = z;
            selfComponent.transform.localScale = localScale;
            return selfComponent;
        }
        /// <summary>获取 localScale.z。</summary>
        public static float LocalScaleZ(this GameObject self)
        {
            return self.transform.localScale.z;
        }

        #endregion

        #region 组件获取与添加

        /// <summary>
        /// 获取指定组件；不存在（含组件已销毁的 fake-null）时自动添加，返回该组件。
        /// 副作用：可能执行 AddComponent。
        /// </summary>
        public static T GetOrAddComponent<T>(this GameObject self) where T : Component
        {
            var comp = self.gameObject.GetComponent<T>();
            return comp ? comp : self.gameObject.AddComponent<T>();
        }
        /// <summary>
        /// 获取指定类型组件；不存在（含组件已销毁的 fake-null）时自动添加，返回该组件。
        /// 副作用：可能执行 AddComponent；type 非法时由 Unity 抛异常。
        /// </summary>
        public static Component GetOrAddComponent(this GameObject self, Type type)
        {
            var component = self.gameObject.GetComponent(type);
            return component ? component : self.gameObject.AddComponent(type);
        }

        #endregion

        #region 子物体设置

        /// <summary>销毁所有直接子物体（Unity 中延迟到帧末生效），返回原对象。</summary>
        public static GameObject DestroyChildren(this GameObject selfGameObj)
        {
            var childCount = selfGameObj.transform.childCount;
            for (var i = childCount - 1; i >= 0; i--)
            {
                selfGameObj.transform.GetChild(i).DestroyGameObjGracefully();
            }
            return selfGameObj;
        }
        /// <summary>销毁满足条件的直接子物体（Unity 中延迟到帧末生效），返回原对象。</summary>
        public static GameObject DestroyChildren(this GameObject self, Func<Transform, bool> condition)
        {
            var childCount = self.transform.childCount;
            for (var i = childCount - 1; i >= 0; i--)
            {
                var child = self.transform.GetChild(i);
                if (condition(child))
                {
                    child.DestroyGameObjGracefully();
                }
            }
            return self;
        }
        /// <summary>把自身移到同级末尾，返回原对象。</summary>
        public static GameObject AsLastSibling(this GameObject self)
        {
            self.transform.SetAsLastSibling();
            return self;
        }
        /// <summary>把自身移到同级开头，返回原对象。</summary>
        public static GameObject AsFirstSibling(this GameObject self)
        {
            self.transform.SetAsFirstSibling();
            return self;
        }
        /// <summary>把自身移到指定同级索引，返回原对象。</summary>
        public static GameObject AsIndexSibling(this GameObject self, int index)
        {
            self.transform.SetSiblingIndex(index);
            return self;
        }

        /// <summary>把 child 挂到自身下，返回自身。</summary>
        public static GameObject AddChild(this GameObject self, Transform child)
        {
            child.parent = self.transform;
            return self;
        }

        /// <summary>把 child 挂到自身下，返回自身。</summary>
        public static GameObject AddChild(this GameObject self, GameObject child)
        {
            child.transform.parent = self.transform;
            return self;
        }

        /// <summary>把 child 挂到自身下，并按 isLastOrFirstSibling 决定移到末尾或开头，返回自身。</summary>
        public static GameObject AddChild(this GameObject self, Transform child, bool isLastOrFirstSibling)
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
            return self;
        }

        /// <summary>把 child 挂到自身下并设置同级索引，返回自身。</summary>
        public static GameObject AddChild(this GameObject self, Transform child, int index)
        {
            child.parent = self.transform;
            child.SetSiblingIndex(index);
            return self;
        }

        #endregion
    }
}

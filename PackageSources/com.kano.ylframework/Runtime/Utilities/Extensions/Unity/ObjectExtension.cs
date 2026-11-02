using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// UnityEngine.Object 扩展：实例化、命名与销毁。
    /// Gracefully 系列使用 Unity fake-null 语义：C# 引用非 null 但已被 Destroy 的对象会被跳过；
    /// 其他销毁方法直接转交 Object.Destroy，不额外判空。
    /// 克隆对象不表示复制其引用的共享资源，销毁克隆体也不负责释放这些共享资源。
    /// </summary>
    public static class ObjectExtension
    {
        /// <summary>判断 Unity 对象是否为 null 或已销毁；不使用托管引用判空。</summary>
        public static bool IsDestroy(this Object selfObj) => !selfObj;

        /// <summary>
        /// 克隆对象（等价于 Object.Instantiate）；原对象不变，返回克隆体。
        /// </summary>
        public static T Instantiate<T>(this T selfObj) where T : Object
        {
            return Object.Instantiate(selfObj);
        }

        /// <summary>
        /// 在指定世界位置与世界旋转克隆对象，返回克隆体。
        /// </summary>
        public static T Instantiate<T>(this T selfObj, Vector3 position, Quaternion rotation)
            where T : Object
        {
            return Object.Instantiate(selfObj, position, rotation);
        }


        /// <summary>
        /// 在指定世界位置与世界旋转克隆对象，并挂到 parent 下；parent 可为 null。
        /// </summary>
        public static T Instantiate<T>(this T selfObj, Vector3 position, Quaternion rotation, Transform parent)
            where T : Object
        {
            return Object.Instantiate(selfObj, position, rotation, parent);
        }


        /// <summary>
        /// 克隆对象并挂到 parent 下，按 worldPositionStays 决定保留世界变换还是局部变换；parent 可为 null（挂到场景根）。
        /// </summary>
        public static T InstantiateWithParent<T>(this T selfObj, Transform parent, bool worldPositionStays)
            where T : Object
        {
            return (T)Object.Instantiate((Object)selfObj, parent, worldPositionStays);
        }

        /// <summary>
        /// 克隆对象并挂到 parent 下，按 worldPositionStays 决定保留世界还是局部变换；parent 无效时挂到场景根。
        /// </summary>
        public static T InstantiateWithParent<T>(this T selfObj, Component parent, bool worldPositionStays)
            where T : Object
        {
            // 已销毁的 parent 按 Unity fake-null 视为 null，避免访问 parent.transform 抛异常
            var parentTransform = parent == null ? null : parent.transform;
            return (T)Object.Instantiate((Object)selfObj, parentTransform, worldPositionStays);
        }

        /// <summary>
        /// 克隆对象并挂到 parent 下（worldPositionStays = false，保留局部变换）；parent 可为 null。
        /// </summary>
        public static T InstantiateWithParent<T>(this T selfObj, Transform parent) where T : Object
        {
            return Object.Instantiate(selfObj, parent, false);
        }

        /// <summary>
        /// 克隆对象并挂到 parent 下（worldPositionStays = false）；parent 为 null 或已销毁时挂到场景根。
        /// </summary>
        public static T InstantiateWithParent<T>(this T selfObj, Component parent) where T : Object
        {
            // 已销毁的 parent 按 Unity fake-null 视为 null
            var parentTransform = parent == null ? null : parent.transform;
            return Object.Instantiate(selfObj, parentTransform, false);
        }


        /// <summary>
        /// 设置对象名字，返回原对象。
        /// </summary>
        public static T Name<T>(this T selfObj, string name) where T : Object
        {
            selfObj.name = name;
            return selfObj;
        }


        /// <summary>
        /// 销毁对象（Unity 中延迟到帧末生效）；不做判空，属于强制操作。
        /// </summary>
        public static void DestroySelf<T>(this T selfObj) where T : Object
        {
            Object.Destroy(selfObj);
        }

        /// <summary>
        /// 销毁对象（延迟到帧末生效）；对象为 null 或已销毁时跳过，返回原对象。
        /// </summary>
        public static T DestroySelfGracefully<T>(this T selfObj) where T : Object
        {
            if (selfObj)
            {
                Object.Destroy(selfObj);
            }

            return selfObj;
        }

        /// <summary>
        /// 延迟 afterDelay 个受 Time.timeScale 影响的秒后销毁对象，返回原对象。
        /// </summary>
        public static T DestroySelfWithDelay<T>(this T selfObj, float afterDelay) where T : Object
        {
            Object.Destroy(selfObj, afterDelay);
            return selfObj;
        }

        /// <summary>
        /// 按受 Time.timeScale 影响的 delay 秒延迟销毁对象；对象为 null 或已销毁时跳过，返回原对象。
        /// </summary>
        public static T DestroySelfWithDelayGracefully<T>(this T selfObj, float delay) where T : Object
        {
            if (selfObj)
            {
                Object.Destroy(selfObj, delay);
            }

            return selfObj;
        }

        /// <summary>
        /// 请求跨场景保留根 GameObject 或其组件，并保留整个子层级；适用限制由 Unity 检查，返回原对象。
        /// </summary>
        public static T DontDestroyOnLoad<T>(this T selfObj) where T : Object
        {
            Object.DontDestroyOnLoad(selfObj);
            return selfObj;
        }
    }
}

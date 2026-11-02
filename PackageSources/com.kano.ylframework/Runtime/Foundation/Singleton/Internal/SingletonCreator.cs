using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YLFramework.Singletons
{
    /// <summary>集中实现普通单例的构造，以及 MonoBehaviour 单例的场景采用和路径创建策略。</summary>
    internal static class SingletonCreator
    {
        /// <summary>
        /// 通过无参构造函数创建实例（public / private / protected 均可）；
        /// 抽象类型、缺少无参构造函数或构造函数抛异常时抛出带类型信息的异常。
        /// </summary>
        private static T CreateNonPublicConstructorObject<T>() where T : class
        {
            var type = typeof(T);
            if (type.IsAbstract)
            {
                throw new InvalidOperationException(
                    $"[Singleton] 无法创建 {type.FullName}：类型是抽象的，单例类型必须是可实例化的具体类型。");
            }

            var constructors =
                type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var constructor = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
            if (constructor == null)
            {
                throw new MissingMethodException(
                    $"[Singleton] 无法创建 {type.FullName}：未找到无参构造函数，请为单例类型提供无参构造函数。");
            }

            try
            {
                return constructor.Invoke(null) as T;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw new InvalidOperationException(
                    $"[Singleton] 创建 {type.FullName} 失败：构造函数抛出异常 {e.InnerException.GetType().Name}。",
                    e.InnerException);
            }
        }

        /// <summary>
        /// 创建单例实例：MonoBehaviour 派生类型走场景查找/创建流程（<see cref="CreateMonoSingleton{T}"/>），
        /// 其他类型使用无参构造函数创建；创建成功后调用 OnSingletonInit。
        /// </summary>
        public static T CreateSingleTon<T>() where T : class, ISingleton
        {
            var type = typeof(T);
            var monoBehaviourType = typeof(MonoBehaviour);

            if (monoBehaviourType.IsAssignableFrom(type))
            {
                return CreateMonoSingleton<T>();
            }

            var instance = CreateNonPublicConstructorObject<T>();
            instance?.OnSingletonInit();
            return instance;
        }

        /// <summary>
        /// 查找或创建 MonoBehaviour 单例。
        /// 先按 InstanceID 升序扫描场景（可选包含未激活对象），取第一个未被排除的匹配实例；
        /// 存在多个匹配时输出警告，但不会销毁其余实例（去重策略由 PersistentMonoSingleton / ReplaceableMonoSingleton 等子类决定）。
        /// 场景中没有时按 MonoSingletonPathAttribute 指定的路径查找/创建，仍无则新建以类型名命名的 GameObject。
        /// 非播放模式下默认返回 null（allowEditMode 为 true 时允许）；应用退出流程中不再创建。
        /// </summary>
        /// <param name="excludedInstance">需要跳过的实例（例如同帧内刚被 Dispose、尚未实际销毁的对象）。</param>
        /// <param name="allowEditMode">非播放模式下是否允许查找/创建。</param>
        /// <param name="includeInactive">查找时是否包含未激活对象。</param>
        /// <param name="persistent">采用/创建顶级实例时是否 DontDestroyOnLoad。</param>
        /// <param name="onCreated">未采用场景候选而走路径或新建流程后调用的回调；路径复用已有组件时也会调用。</param>
        /// <param name="isExcluded">自定义排除判定，返回 true 的实例会被跳过。</param>
        public static T CreateMonoSingleton<T>(T excludedInstance = null, bool allowEditMode = false,
            bool includeInactive = false, bool persistent = false, Action<T> onCreated = null,
            Predicate<T> isExcluded = null) where T : class, ISingleton
        {
            var type = typeof(T);
            if (!Application.isPlaying && !allowEditMode)
            {
                return null;
            }

            if (Application.isPlaying && SingletonSessionState.IsApplicationQuitting)
            {
                return null;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                return null;
            }

            //从场景找实例；excludedInstance 为同帧内刚被 Dispose 的对象，需要跳过（Unity 尚未完成实际销毁）
            var candidates = Object.FindObjectsByType(type,
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            //按 InstanceID 升序排序，保证同一状态下重复访问的选择结果稳定（不承诺创建顺序）
            Array.Sort(candidates, (left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
            T firstMatch = null;
            var matchCount = 0;
            foreach (var candidateObject in candidates)
            {
                if (candidateObject == null)
                {
                    continue;
                }

                var sceneInstance = candidateObject as T;
                if (sceneInstance == null || ReferenceEquals(sceneInstance, excludedInstance) ||
                    (isExcluded != null && isExcluded(sceneInstance)))
                {
                    continue;
                }

                if (firstMatch == null)
                {
                    firstMatch = sceneInstance;
                }

                matchCount++;
            }

            if (matchCount > 1)
            {
                Debug.LogWarning(
                    $"[Singleton] 场景中存在 {matchCount} 个 {type.FullName} 实例，普通 MonoSingleton 未定义重复处理策略，" +
                    "Instance 将稳定地固定采用其中一个，且不会销毁其余实例。如需保留先创建实例请使用 PersistentMonoSingleton，如需后创建替换请使用 ReplaceableMonoSingleton。");
            }

            if (firstMatch != null)
            {
                var component = firstMatch as Component;
                if (persistent && Application.isPlaying && component.transform.parent == null)
                {
                    Object.DontDestroyOnLoad(component.gameObject);
                }

                firstMatch.OnSingletonInit();
                return firstMatch;
            }

            var instance = CreateByPathAttribute<T>(type, isExcluded);
            if (instance == null)
            {
                var obj = new GameObject(type.Name);
                instance = obj.AddComponent(type) as T;
            }

            if (instance != null)
            {
                onCreated?.Invoke(instance);
                var component = instance as Component;
                if (persistent && Application.isPlaying && component.transform.parent == null)
                {
                    Object.DontDestroyOnLoad(component.gameObject);
                }
            }
            instance?.OnSingletonInit();
            return instance;
        }

        /// <summary>读取类型继承链上的路径特性，存在时按该路径取得组件，否则返回 null。</summary>
        private static T CreateByPathAttribute<T>(Type type, Predicate<T> isExcluded) where T : class
        {
            var pathAttribute = Attribute.GetCustomAttribute(type, typeof(MonoSingletonPathAttribute), true)
                as MonoSingletonPathAttribute;
            return pathAttribute == null
                ? null
                : CreateGameObjectOfMonoSingletonByPath<T>(pathAttribute.HierarchyPath, pathAttribute.IsDontDestroy, isExcluded);
        }

        /// <summary>
        /// 沿路径取得或创建承载节点；已有组件被排除时，在同级替换节点上添加新组件。
        /// </summary>
        private static T CreateGameObjectOfMonoSingletonByPath<T>(string path, bool dontDestroy,
            Predicate<T> isExcluded) where T : class
        {
            var obj = FindGameObject(path, true, dontDestroy);
            if (obj == null)
            {
                obj = new GameObject($"{typeof(T).Name} Singleton");
                if (dontDestroy)
                {
                    Object.DontDestroyOnLoad(obj);
                }
            }

            var existing = obj.GetComponent(typeof(T)) as T;
            if (existing != null)
            {
                if (isExcluded == null || !isExcluded(existing))
                {
                    return existing;
                }
                // 被排除的组件所在节点可能一起等待销毁，改为在同级创建替换节点承载新组件
                var replacement = new GameObject(obj.name);
                replacement.transform.SetParent(obj.transform.parent, false);
                if (dontDestroy && replacement.transform.parent == null)
                {
                    Object.DontDestroyOnLoad(replacement);
                }

                obj = replacement;
            }
            return obj.AddComponent(typeof(T)) as T;
        }

        #region 根据路径找/创建 某个物体

        /// <summary>拆分非空层级路径并启动逐级查找，可选补建缺失节点。</summary>
        private static GameObject FindGameObject(string path, bool build, bool dontDestroy)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var subPath = path.Split('/');
            if (subPath == null || subPath.Length == 0)
            {
                return null;
            }

            return FindGameObject(null, subPath, 0, build, dontDestroy);
        }

        /// <summary>
        /// 从指定层级逐级查找目标节点；允许补建时仅对新建的路径根节点应用常驻设置。
        /// </summary>
        /// <returns>目标物体</returns>
        private static GameObject FindGameObject(GameObject root, string[] subPath, int index, bool build,
            bool dontDestroy)
        {
            while (true)
            {
                GameObject client = null;

                if (root == null)
                {
                    client = GameObject.Find(subPath[index]);
                }
                else
                {
                    var child = root.transform.Find(subPath[index]);
                    if (child != null)
                    {
                        client = child.gameObject;
                    }
                }

                if (client == null && build)
                {
                    client = new GameObject(subPath[index]);
                    if (root != null)
                    {
                        client.transform.SetParent(root.transform);
                    }

                    if (dontDestroy && index == 0)
                    {
                        Object.DontDestroyOnLoad(client);
                    }
                }

                if (client == null)
                {
                    return null;
                }

                if (++index == subPath.Length)
                {
                    return client;
                }

                root = client;
            }
        }

        #endregion
    }
}

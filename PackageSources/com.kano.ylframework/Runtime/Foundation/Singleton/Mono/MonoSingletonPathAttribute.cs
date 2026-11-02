using System;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 指定 MonoSingleton 在 Hierarchy 中的查找/创建路径：
    /// 未采用场景候选时，才沿该路径逐级查找或补建承载 GameObject。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MonoSingletonPathAttribute : Attribute
    {
        /// <summary>
        /// 层级路径，例如 "YLFramework/Pool/GameObjectPool"（'/' 分隔）。
        /// </summary>
        public string HierarchyPath
        {
            get; private set;
        }

        /// <summary>
        /// 路径根节点是否 DontDestroyOnLoad，默认 true。
        /// </summary>
        public bool IsDontDestroy
        {
            get; private set;
        }

        /// <summary>
        /// 创建路径特性。
        /// </summary>
        /// <param name="hierarchyPath">层级路径，'/' 分隔；为空时无法沿路径查找，改在备用节点上添加组件。</param>
        /// <param name="isDontDestroy">路径根节点是否 DontDestroyOnLoad。</param>
        public MonoSingletonPathAttribute(string hierarchyPath, bool isDontDestroy = true)
        {
            HierarchyPath = hierarchyPath;
            IsDontDestroy = isDontDestroy;
        }
    }
}

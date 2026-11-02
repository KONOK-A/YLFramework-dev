using System;

namespace YLFramework.Attributes
{
    /// <summary>
    /// 标记需要自定义 Editor 绘制的序列化字段。
    /// </summary>
    /// <remarks>
    /// 抽象特性，仅供派生：编辑器下派生类必须实现 <c>OnPropertyGui</c>，由外部编辑器代码读取字段特性后调用；
    /// Player 构建中不包含该方法，派生类中的重写代码需放在 <c>#if UNITY_EDITOR</c> 内。
    /// 框架的领域 Inspector 可读取该特性；Player 中只保留元数据，不引入 UnityEditor 引用。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public abstract class CustomPropertyGui : Attribute
    {
        /// <summary>字段值改变时是否请求接入方重新加载 TreeView；在 Editor 与 Player 中均可配置。</summary>
        public bool ReloadTreeViewOnValueChange;

        /// <summary>创建默认不请求 TreeView 重载的字段绘制特性。</summary>
        public CustomPropertyGui() : this(false) { }

        /// <summary>记录字段绘制后的刷新请求，具体变更检测与刷新由接入方执行。</summary>
        /// <param name="reloadTreeViewOnValueChange">字段值改变时是否请求接入方重新加载 TreeView。</param>
        public CustomPropertyGui(bool reloadTreeViewOnValueChange)
        {
            this.ReloadTreeViewOnValueChange = reloadTreeViewOnValueChange;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 在编辑器中绘制当前字段。
        /// </summary>
        /// <param name="serializedProperty">字段对应的序列化属性。</param>
        /// <remarks>由接入 Inspector 显式调用；序列化变更的应用与 TreeView 刷新仍由该 Inspector 负责。</remarks>
        public abstract void OnPropertyGui(UnityEditor.SerializedProperty serializedProperty);
#endif
    }
}

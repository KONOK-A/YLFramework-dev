using System;
using YLFramework.Foundation;


namespace YLFramework.Buff
{
    /// <summary>为效果配置类型提供作者菜单与分组使用的显示名称。</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class EffectName : Attribute
    {

        private string _name;

        /// <summary>该效果类型的显示名称。</summary>
        public string Name => _name;

        /// <summary>指定效果类型在作者工具中的显示名称。</summary>
        /// <param name="name">菜单或分组名称。</param>
        public EffectName(string name)
        {
            this._name = name;
        }
    }

}

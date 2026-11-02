using System;
using System.Collections.Generic;

namespace YLFramework.Editor.Help
{
    /// <summary>一个显式注册的模块；内容在选中时才建立。</summary>
    public sealed class HelpRegistration
    {
        /// <summary>稳定模块标识。</summary>
        public readonly string Id;
        /// <summary>模块选择器中的名称。</summary>
        public readonly string DisplayName;
        private readonly Func<HelpModule> _create;

        /// <summary>保存已由注册入口校验的身份和工厂引用，不提前创建内容。</summary>
        internal HelpRegistration(string id, string displayName, Func<HelpModule> create)
        {
            Id = id;
            DisplayName = displayName;
            _create = create;
        }

        /// <summary>调用提供方的内容工厂；注册项自身不缓存返回结果。</summary>
        /// <returns>提供方返回的帮助模块。</returns>
        /// <exception cref="InvalidOperationException">内容工厂返回 null。</exception>
        public HelpModule Create() => _create() ?? throw new InvalidOperationException("帮助模块没有返回内容：" + Id);
    }

    /// <summary>项目内部帮助注册表，不扫描程序集或业务模块。</summary>
    public static class HelpRegistry
    {
        private static readonly List<HelpRegistration> _modules = new List<HelpRegistration>();
        /// <summary>注册集合变化时同步通知窗口，使其在下一次绘制前重新读取快照。</summary>
        internal static event Action Changed;

        /// <summary>注册一个内容工厂；相同标识替换原注册，保持原显示顺序。</summary>
        /// <param name="id">非空的稳定模块标识，按大小写精确区分。</param>
        /// <param name="displayName">模块选择器显示的非空名称。</param>
        /// <param name="create">非 null 的内容工厂；注册时不会调用。</param>
        /// <remarks>提供方应在自身 Editor 程序集初始化时显式注册；本注册表不发现或加载业务程序集。</remarks>
        /// <exception cref="ArgumentException">id 或 displayName 为 null、空或仅由空白字符组成。</exception>
        /// <exception cref="ArgumentNullException">create 为 null。</exception>
        public static void Register(string id, string displayName, Func<HelpModule> create)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("模块标识不能为空。", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException("模块名称不能为空。", nameof(displayName));
            }

            if (create == null)
            {
                throw new ArgumentNullException(nameof(create));
            }

            var index = _modules.FindIndex(module => module.Id == id);
            var registration = new HelpRegistration(id, displayName, create);
            if (index < 0)
            {
                _modules.Add(registration);
            }
            else
            {
                _modules[index] = registration;
            }

            Changed?.Invoke();
        }

        /// <summary>移除一个提供方；不存在时返回 false。</summary>
        /// <param name="id">要移除的模块标识，按大小写精确匹配。</param>
        /// <returns>实际移除了注册项时为 true；只有移除成功才触发注册表变更通知。</returns>
        public static bool Unregister(string id)
        {
            var removed = _modules.RemoveAll(module => module.Id == id) != 0;
            if (removed)
            {
                Changed?.Invoke();
            }

            return removed;
        }

        /// <summary>取得注册快照，不调用内容工厂。</summary>
        /// <returns>按注册顺序排列的新数组；修改数组不影响注册表。</returns>
        public static HelpRegistration[] GetModules() => _modules.ToArray();
    }
}

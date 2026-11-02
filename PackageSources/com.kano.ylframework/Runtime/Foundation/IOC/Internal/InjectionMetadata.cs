using System;
using System.Linq;
using System.Reflection;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 单个带 <see cref="InjectAttribute"/> 成员的描述（字段或属性）。
    /// </summary>
    internal sealed class InjectableMember
    {
        internal MemberInfo Member;

        internal Type MemberType;

        internal string InjectName;

        // 属性只要任一访问器公开就归入公开成员，实际写入仍使用其 setter。
        internal bool IsPublic;

        internal bool CanInject;

        internal string InvalidReason;

        internal string KindName => Member is FieldInfo ? "field" : "property";
    }

    /// <summary>
    /// 单个类型的可注入成员清单（按声明层级从基类到派生类、同层字段先于属性排列，顺序确定但非公开契约）。
    /// 按注入模式惰性缓存过滤结果，避免每次 Inject 重新扫描反射。
    /// </summary>
    internal sealed class InjectionMetadata
    {
        private readonly InjectableMember[] _orderedMembers;
        private InjectableMember[] _onlyPublic;
        private InjectableMember[] _onlyNonPublic;

        /// <summary>保存已排序的成员描述，后续只按可见性筛选，不重复扫描反射。</summary>
        internal InjectionMetadata(InjectableMember[] orderedMembers)
        {
            _orderedMembers = orderedMembers;
        }

        /// <summary>
        /// 按搜索模式返回本次要处理的成员：OnlyPublic 只含公开成员，OnlyNonPublic 只含非公开成员，All 为全部。
        /// </summary>
        internal InjectableMember[] GetMembers(IOCContainer.MemberSearchModes mode)
        {
            switch (mode)
            {
                case IOCContainer.MemberSearchModes.OnlyPublic:
                    return _onlyPublic ?? (_onlyPublic = Filter(true));
                case IOCContainer.MemberSearchModes.OnlyNonPublic:
                    return _onlyNonPublic ?? (_onlyNonPublic = Filter(false));
                default:
                    return _orderedMembers;
            }
        }

        /// <summary>按可见性生成筛选结果，并保留原清单的注入顺序。</summary>
        private InjectableMember[] Filter(bool isPublic) =>
            _orderedMembers.Where(member => member.IsPublic == isPublic).ToArray();
    }
}

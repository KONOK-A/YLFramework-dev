using System.Collections.Generic;
using YLFramework.Foundation;

namespace YLFramework.Skill
{
    /// <summary>
    /// 可装备技能的接口,如果一个游戏角色能够装备技能和释放技能,请实现该接口
    /// </summary>
    /// <remarks>普通 CLR 宿主结束使用时须显式调用 SkillManager.CloseHost；实现接口本身不会自动绑定生命周期。</remarks>
    public interface IEquippableSkill
    {

        /// <summary>
        /// 宿主持有的可变技能字典，须预先初始化；正常装备和移除通过 SkillManager 维护。
        /// </summary>
        Dictionary<int, SkillBase> Skills
        {
            get;
        }


        /// <summary>
        /// 是否能够释放技能
        /// </summary>
        /// <returns>宿主当前是否允许发起释放；不代替单个技能的冷却和并行检查。</returns>
        bool CanRelease();

    }

}

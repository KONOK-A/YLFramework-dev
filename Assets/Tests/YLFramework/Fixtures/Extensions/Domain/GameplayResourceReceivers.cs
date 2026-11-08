using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Skill;

namespace YLFramework.Tests.DomainAcceptance
{
    // 配置仅以字符串引用三种派生类；消费者自己的定向link.xml保留它们，不扩大Runtime裁剪范围。
    public sealed class ReceiverSkill : SkillBase
    {
    }
    public sealed class ReceiverBuff : BuffBase
    {
    }
    public sealed class ReceiverMission : MissionBase
    {
    }
}

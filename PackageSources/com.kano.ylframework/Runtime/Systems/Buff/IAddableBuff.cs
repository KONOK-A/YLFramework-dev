using System.Collections.Generic;
using YLFramework.Foundation;

namespace YLFramework.Buff
{
    /// <summary>
    /// 为 BuffManager 提供宿主列表、添加许可与通知；宿主结束时须显式调用 CloseHost。
    /// </summary>
    public interface IAddableBuff
    {
        /// <summary>
        /// 已初始化且由宿主持有的运行实例列表；通过管理器增删以同步效果索引与生命周期。
        /// </summary>
        List<BuffBase> Buffs
        {
            get;
        }

        /// <summary>
        /// 判断是否能够添加某个buff
        /// </summary>
        /// <param name="buffId">本次请求添加的 Buff ID。</param>
        /// <returns>是否允许管理器继续创建候选实例。</returns>
        bool CanAdd(int buffId);

        /// <summary>
        /// 添加Buff回调
        /// </summary>
        /// <param name="buff">已入列表且完成 OnAdd、仍有效的运行实例。</param>
        void OnAddBuff(BuffBase buff);

        /// <summary>
        /// 移除Buff回调
        /// </summary>
        /// <param name="buff">已出列表、尚未归还的实例；仅在回调期间读取本次数据，不保留作后续使用。</param>
        void OnRemoveBuff(BuffBase buff);
    }
}

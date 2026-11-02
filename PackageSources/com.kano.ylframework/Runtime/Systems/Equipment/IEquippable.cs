using System.Collections.Generic;

namespace YLFramework.Equipment
{
    /// <summary>宿主稳定持有已初始化的列表，通过 Manager 修改；结束时显式 CloseHost。</summary>
    public interface IEquippable
    {
        /// <summary>由宿主稳定持有的已初始化装备列表，通过管理器增删而非直接改动。</summary>
        List<IEquipment> Equipments
        {
            get;
        }
        /// <summary>在挂接前判断是否允许装备；管理器不会代为实现部位、数量或属性条件。</summary>
        /// <param name="equipment">待挂接的运行实例，不是配置定义。</param>
        /// <returns>是否允许继续本次挂接。</returns>
        bool CanEquip(IEquipment equipment);
    }
}

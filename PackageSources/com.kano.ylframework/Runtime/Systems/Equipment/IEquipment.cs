namespace YLFramework.Equipment
{
    /// <summary>一次 Equip 请求完成后的挂接结果，不表示装备对象的所有权发生转移。</summary>
    public enum EquipState
    {
        /// <summary>装备通知完成后，本次挂接仍有效且未请求释放。</summary>
        Success = 0,
        /// <summary>宿主拒绝，或本次挂接在生命周期回调中被取消。</summary>
        CannotEquip = 1,
        /// <summary>实例已挂接、已有穿戴者，或目标宿主列表中已包含同一引用。</summary>
        AlreadyEquipped = 2
    }

    /// <summary>单件装备。定义只读共享；实例状态由实现类维护，归还后不得继续使用旧引用。</summary>
    public interface IEquipment
    {
        /// <summary>当前穿戴者，由管理器设置和清除；实现方应保留管理器写入的关联。</summary>
        IEquippable Equipper
        {
            get; set;
        }
        /// <summary>共享装备定义，不用于存放本实例的耐久、冷却等可变业务状态。</summary>
        EquipmentConfigData EquipmentData
        {
            get; set;
        }
        /// <summary>自有实例取得后调用，此时本次 EquipmentData 已设置。</summary>
        void OnLoaded();
        /// <summary>自有实例归还前清理本次状态；外部借用实例不触发。</summary>
        void OnUnloaded();
        /// <summary>挂接后应用本次装备效果；角色属性增益等业务行为须由实现方处理。</summary>
        void OnEquip();
        /// <summary>卸下时撤销本次装备效果；管理器不自动反向计算属性变化。</summary>
        void OnUnequip();
    }
}

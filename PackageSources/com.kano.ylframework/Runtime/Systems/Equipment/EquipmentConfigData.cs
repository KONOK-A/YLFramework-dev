using System;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Excel;

namespace YLFramework.Equipment
{
    /// <summary>保存装备图标的资源地址，与装备 Prefab 自身的地址独立。</summary>
    [Serializable]
    public class IconInfo
    {
        /// <summary>图标所属的 YL 资源项目标识。</summary>
        [Tooltip("YL Resource ProjectId")] public string projectName;
        /// <summary>图标在资源项目中的路径。</summary>
        [Tooltip("YL Resource Path")] public string iconName;
    }

    /// <summary>决定管理器如何取得装备运行实例。</summary>
    public enum EquipmentType
    {
        /// <summary>通过 ResourceSystem 实例化 Prefab，根节点须恰有一个 IEquipment 组件。</summary>
        Asset = 0,
        /// <summary>从对象池取得普通 IEquipment 类，要求 public 无参构造且不能继承 UnityEngine.Object。</summary>
        Script = 1
    }

    /// <summary>共享装备定义；已登记期间不可修改，运行态数据属于装备实例。</summary>
    public class EquipmentConfigData : ScriptableObject
    {
        /// <summary>定义所属的装备配表。</summary>
        [ExcelIgnore] public EquipmentsConfig parent;
        /// <summary>跨已登记配表唯一的装备 ID。</summary>
        [ExcelScriptableObjectId] public int id;
        /// <summary>装备的显示名称。</summary>
        [FormerlySerializedAs("equipment_name")] public string equipmentName;
        /// <summary>装备图标的资源地址信息。</summary>
        public IconInfo icon = new();
        /// <summary>供作者工具或业务界面展示的说明文本。</summary>
        [TextArea(3, 8)] public string description;
        /// <summary>运行实例的取得方式，决定使用脚本类型还是 Prefab 地址。</summary>
        public EquipmentType equipmentType;
        /// <summary>Script 模式下用于反射创建的普通装备类全名。</summary>
        [FormerlySerializedAs("equipment_class_full_name")] public string equipmentClassFullName;
        /// <summary>编辑器维护脚本类型绑定的 GUID，运行时不按此字段加载。</summary>
        [HideInInspector, ExcelIgnore, FormerlySerializedAs("equipment_class_guid")] public string equipmentClassGuid;
        /// <summary>Asset 模式下装备 Prefab 所属的资源项目标识。</summary>
        [Tooltip("YL Resource ProjectId")] public string projectName;
        /// <summary>Asset 模式下装备 Prefab 的正式资源路径。</summary>
        [Tooltip("YL Resource Path")] public string assetName;
    }
}

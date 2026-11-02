using System;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Excel;
using YLFramework.Resource;

namespace YLFramework.DataAssets
{
    /// <summary>配置条目的图标定位信息，仅描述资源项目与地址，不持有加载结果。</summary>
    [Serializable]
    public class IconInfo
    {
        /// <summary>图标所在 YL Resource 项目的显式标识。</summary>
        [Tooltip("YL Resource ProjectId")]
        public string projectName;
        /// <summary>图标在资源目录中的逻辑地址。</summary>
        [Tooltip("YL Resource address")]
        public string iconName;

        /// <summary>将非空项目标识和图标地址组合为资源地址，不加载图标。</summary>
        /// <param name="address">成功时为构造的地址，字段为空时为默认值。</param>
        /// <returns>项目标识与图标地址均非空且地址构造成功时为真。</returns>
        public bool TryGetAddress(out ResourceAddress address)
        {
            address = default;
            if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(iconName))
            {
                return false;
            }

            address = new ResourceAddress(projectName, iconName);
            return true;
        }
    }

    /// <summary>配置定义；查询返回借读引用，不在运行时修改共享资产。</summary>
    public class ScriptableObjectData : ScriptableObject
    {
        /// <summary>所属配置根；编辑器据此结合实际资产位置判断条目所有权。</summary>
        [HideInInspector, ExcelIgnore]
        public ScriptableObjectConfigs parent;
        /// <summary>根内唯一的业务 ID，不以文件名或列表槽位作为查询键。</summary>
        [Tooltip("Unique ID within the root")]
        public int id;
        /// <summary>供显示与精确名称查询使用的配置名称，不等同于资产对象名。</summary>
        [FormerlySerializedAs("config_name")] public string configName;
        /// <summary>条目的图标定位信息，由使用方另行取得图片资源。</summary>
        public IconInfo icon = new IconInfo();
        /// <summary>条目说明，编辑器列表将其用作名称提示。</summary>
        [TextArea(3, 10)]
        [FormerlySerializedAs("des")] public string description;

        /// <summary>编辑器校验条目时使父根索引失效，后续查询据当前数据重建。</summary>
        protected virtual void OnValidate()
        {
            if (parent)
            {
                parent.ClearCache();
            }
        }
    }
}

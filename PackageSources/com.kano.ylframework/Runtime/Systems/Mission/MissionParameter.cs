using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Events;
using YLFramework.Foundation;

namespace YLFramework.Mission
{
    /// <summary>
    /// 任务参数类型
    /// </summary>
    public enum MissionParamType
    {
        /// <summary>以浮点数参与比较的参数。</summary>
        Float,
        /// <summary>条件比较时四舍五入为整数的参数，底层仍使用浮点数保存。</summary>
        Int,
        /// <summary>由管理器以 0 和 1 表达假、真的参数。</summary>
        Bool
    }

    /// <summary>
    /// 任务共享参数，保存配置约束、运行值及条件和业务通知。
    /// </summary>
    /// <remarks>管理器复制配置后持有运行参数；写入限制由管理器执行，直接设置 Value 不检查这些限制。</remarks>
    [Serializable]
    public class MissionParameter
    {

        #region 字段

        /// <summary>
        /// 参数名称
        /// </summary>
        [Tooltip("参数名称")]
        [Header("参数名称")]
        [YLFramework.Excel.ExcelScriptableObjectId]
        public string name;

        /// <summary>
        /// 参数类型
        /// </summary>
        [Tooltip("参数类型")]
        [Header("参数类型")]
        public MissionParamType type;

        [Tooltip("当前参数的值")]
        private float _value = 0;

        /// <summary>
        /// 只能在任务执行中设置参数,如果参数对应的任务不在执行中,则无法设置!(默认:true)
        /// </summary>
        /// <remarks>通过管理器写入时，只需同配表中任一引用此参数的任务正在执行即可。</remarks>
        [Tooltip("只能在任务执行中设置参数,如果参数对应的任务不在执行中,则无法设置!(默认:true)")]
        [Header("只能在任务执行中设置参数,如果参数对应的任务不在执行中,则无法设置!(默认:true)")]
        [FormerlySerializedAs("only_can_set_in_progress"), JsonProperty("only_can_set_in_progress")] public bool onlySetInProgress = true;

        /// <summary>
        /// 设置参数时,设置的目标值比当前值大时生效,否则无效(默认:false)
        /// </summary>
        [Tooltip("设置参数时,设置的目标值比当前值大时生效,否则无效(默认:false)")]
        [Header("设置参数时,设置的目标值比当前值大时生效,否则无效(默认:false)")]
        [FormerlySerializedAs("only_can_set_larger"), JsonProperty("only_can_set_larger")] public bool onlySetLarger = false;

        /// <summary>
        /// 参数描述
        /// </summary>
        [Tooltip("参数描述")]
        [Header("参数描述")]
        [TextArea]
        [FormerlySerializedAs("des"), JsonProperty("des")] public string description;

        /// <summary>
        /// 自定义数据
        /// </summary>
        [SerializeField]
        [Tooltip("自定义数据")]
        [Header("自定义数据")]
        [JsonProperty("custom_data"), FormerlySerializedAs("custom_data")]
        private List<string> customData = new List<string>();
        #endregion

        #region 事件

        /// <summary>
        /// 内部使用的事件
        /// </summary>
        [NonSerialized] private EventChannel<MissionParameter> _changes;
        private int _version;
        /// <summary>用于识别参数更新或重置后旧通知的版本。</summary>
        internal int Version => _version;
        /// <summary>登记条件使用的内部通知，返回需由条件释放的订阅凭据。</summary>
        internal IUnregister RegisterChanged(Action<MissionParameter> callback) => (_changes ??= new EventChannel<MissionParameter>()).Register(callback);

        /// <summary>
        /// 值改变的事件
        /// </summary>
        /// <remarks>内部条件通知之后调用；若其间参数被再次写入或重置，则跳过旧版本的本次通知。</remarks>
        [Tooltip("值改变的事件")]
        [JsonIgnore]
        public Action<MissionParameter> ValueChanged;
        #endregion


        #region 属性

        /// <summary>
        /// 当前参数的值
        /// </summary>
        /// <remarks>相同值不通知；先更新值和版本，再同步通知条件与业务订阅，不限制类型、范围或写入策略。</remarks>
        public float Value
        {
            get
            {
                return _value;
            }
            set
            {
                if (this._value.Equals(value))
                {
                    return;
                }

                var version = ++_version;
                this._value = value;
                _changes?.TriggerAll(this);
                if (version == _version)
                {
                    ValueChanged?.Invoke(this);
                }
            }
        }


        #endregion


        #region 方法

        /// <summary>使旧通知失效并清空全部订阅，将运行值静默归零，保留配置。</summary>
        internal void Reset()
        {
            _version++;
            ValueChanged = null;
            _changes?.Dispose();
            _changes = null;
            _value = 0;
        }

        /// <summary>独立复制配置与自定义数据，不复制运行值、监听或通知状态。</summary>
        public MissionParameter CloneConfiguration()
        {
            var clone = (MissionParameter)MemberwiseClone();
            clone._value = 0;
            clone.ValueChanged = null;
            clone._changes = null;
            clone._version = 0;
            clone.customData = customData == null ? new List<string>() : new List<string>(customData);
            return clone;
        }

        /// <summary>
        /// 是否包含自定义数据
        /// </summary>
        /// <param name="customData">自定义数据</param>
        /// <returns>非空输入在自定义数据列表中存在时为 true。</returns>
        public bool ContainsCustomData(string customData)
        {
            if (string.IsNullOrEmpty(customData))
            {
                return false;
            }

            if (this.customData == null || this.customData.Count == 0)
            {
                return false;
            }

            return this.customData.Contains(customData);
        }



        /// <summary>直接保存运行值，不推进版本、不通知订阅，也不重算条件状态。</summary>
        public void SetValueWithoutNotify(float v)
        {
            this._value = v;
        }

        #endregion

    }

}

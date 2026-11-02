using System;

namespace YLFramework.StateMachine
{

    /// <summary>以浮点字段统一存储的参数语义，决定编辑方式和运行时写入入口。</summary>
    public enum ParameterType
    {
        /// <summary>浮点数参数。</summary>
        Float = 0,
        /// <summary>通过整数接口读写、底层仍以浮点数存储的参数。</summary>
        Int,
        /// <summary>以零表示假、一表示真的布尔参数。</summary>
        Bool,
        /// <summary>可被过渡清零并对重复触发排队的触发参数。</summary>
        Trigger
    }

    /// <summary>可序列化的参数定义；运行时在配置克隆中保存当前值与同步变化通知。</summary>
    [System.Serializable]
    public class FsmParameterData
    {
        #region 字段
        /// <summary>用于条件绑定及哈希转换的参数名称。</summary>
        public string name;

        /// <summary>底层存储值，直接修改此字段不会发送变化通知。</summary>
        public float value;

        /// <summary>参数的语义类型。</summary>
        public ParameterType parameterType;

        /// <summary>Value 提交新值后同步调用；任一订阅者抛错会中断后续通知。</summary>
        public Action ValueChanged;

        /// <summary>实例初始化时由参数名称计算的运行时查找键。</summary>
        public int nameHash;

        #endregion

        #region 属性

        /// <summary>读取或写入当前值；新旧值相等时不通知，否则先提交再同步通知。</summary>
        public float Value
        {
            get
            {
                return value;
            }
            set
            {

                if (this.value == value)
                {
                    return;
                }

                this.value = value;
                ValueChanged?.Invoke();
            }
        }

        #endregion


    }

}

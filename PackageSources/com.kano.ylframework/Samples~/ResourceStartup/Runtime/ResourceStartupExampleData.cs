using UnityEngine;

namespace YLFramework.Samples.Resource
{
    /// <summary>独立示例的编译资产类型；Bundle配置必须显式传入typeof(ResourceStartupExampleData)。</summary>
    public sealed class ResourceStartupExampleData : ScriptableObject
    {
        /// <summary>从 data 地址取得示例资产后用于拼接业务结果的文本。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Text")]
        public string text;
    }
}

using UnityEngine;

namespace YLFramework.Samples.Resource
{
    /// <summary>独立样例的编译资产类型；由官方 Entry 与序列化 AssetReference 定位，不运行时登记类型。</summary>
    public sealed class ResourceStartupExampleData : ScriptableObject
    {
        /// <summary>在所属 Scope 存活期间借读的示例文本。</summary>
        public string text;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine.Scripting;
using YLFramework.Core;

namespace YLFramework.Samples.HybridCLR.HotUpdate
{
    /// <summary>由热更入口声明、通过既有 Core 配表路径创建的样例 Model。</summary>
    [Preserve]
    public sealed class ExampleMessage : Model
    {
        /// <summary>样例配置中用于组合版本输出的文本。</summary>
        public string Text
        {
            get; set;
        }
    }

    /// <summary>热更入口 Module，依赖 Support 程序集并向宿主返回同步和异步示例结果。</summary>
    [Preserve]
    public sealed class ExampleModule : Module
    {
        /// <summary>读取宿主本次指定的 Resource 项目身份，忽略外部赋值。</summary>
        public override string ProjectName
        {
            get => HybridCLRExampleState.ProjectId; set
            {
            }
        }
#pragma warning disable CS0672, CS0618
        /// <summary>沿用 Core 的配置预加载入口，将样例 JSON 解释为热更 Model 数组。</summary>
        public override Dictionary<string, Type> PreloadConfigTables => new Dictionary<string, Type>
        {
            { "sample/message.json", typeof(ExampleMessage[]) }
        };
#pragma warning restore CS0672, CS0618
        /// <summary>验证 Core 已创建样例 Model，并调用 Support 中的泛型及 async 能力。</summary>
        public override void OnStart()
        {
            var message = GetModel<ExampleMessage>(1);
            if (message == null)
            {
                throw new InvalidOperationException("The YL Core configuration did not create the sample Model.");
            }

            HybridCLRExampleState.Message = VersionInfo.Version + ": " + message.Text + "; dependency-count=" + VersionInfo.Count(new List<int> { 1, 2 });
            HybridCLRExampleState.Async = VersionInfo.DescribeAsync();
        }
    }
}

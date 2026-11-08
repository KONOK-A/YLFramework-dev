using System;

namespace YLFramework.Editor.Actions
{
    /// <summary>只提供 Action API 的导航分组；成员说明由 Runtime XML 唯一提供。</summary>
    internal static class ActionKitApiHelpReference
    {
        private const string Entry = "Action 入口与契约";
        private const string Actions = "基础与组合动作";
        private const string Execution = "执行与生命周期";
        private const string Tools = "插值与屏幕工具";

        /// <summary>为明确识别的类型选择导航分组，不生成或覆盖成员文档。</summary>
        internal static string TypeGroup(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            switch (Key(type))
            {
                case "ActionKit":
                case "IAction":
                case "AbstractAction":
                case "ActionStatus":
                case "ICustomApi":
                    return Entry;
                case "IActionController":
                case "ActionController":
                case "ActionHandle":
                case "IActionControllerExtensions":
                case "IActionExtensions":
                case "IActionExecutor":
                case "IActionExecutorExtensions":
                case "ActionUpdateModes":
                case "MonoUpdateActionExecutorExtension":
                case "ActionKitCurrentScene":
                    return Execution;
                case "Lerp":
                case "LerpExtension":
                case "EaseUtility":
                case "EaseType":
                case "ActionKit.ScreenTransition":
                case "ScreenTransitionFade":
                case "AudioSourceShortCutExtensions":
                    return Tools;
                case "ISequence":
                case "IParallel":
                case "IRepeat":
                case "Delay":
                case "DelayFrame":
                case "CallbackAction":
                case "ConditionAction":
                case "ConditionSequence":
                case "Sequence":
                case "Parallel":
                case "Repeat":
                case "Custom":
                case "CoroutineAction":
                case "TaskAction":
                case "UniRxAction":
                case "ActionKitUniRxExtension":
                case "DOTweenAction":
                case "ActionKitDOTweenExtension":
                case "DelayExtension":
                case "DelayFrameExtension":
                case "CallbackExtension":
                case "ConditionExtension":
                case "ConditionSequenceExtension":
                case "SequenceExtension":
                case "ParallelExtension":
                case "RepeatExtension":
                case "CustomExtension":
                case "CoroutineExtension":
                case "TaskExtension":
                    return Actions;
                default:
                    return "其他公开类型（说明待核实）";
            }
        }

        private static string Key(Type type)
        {
            if (type == null || (type.Namespace != "YLFramework.Actions" &&
                type.FullName is not ("YLFramework.Integrations.DOTween.DOTweenAction" or
                    "YLFramework.Integrations.DOTween.ActionKitDOTweenExtension")))
            {
                return "";
            }

            var name = type.Name.Split('`')[0];
            return type.IsNested ? Key(type.DeclaringType) + "." + name : name;
        }
    }
}

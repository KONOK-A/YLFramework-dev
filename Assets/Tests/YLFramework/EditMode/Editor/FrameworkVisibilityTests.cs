using System;
using NUnit.Framework;

namespace YLFramework.Tests
{
    public class FrameworkVisibilityTests
    {
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.Context")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.GraphLayer")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.ParamLayer")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.FsmStateNodeFactory")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.FsmTransitionFactory")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.FsmConditionFactory")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.FsmParameterFactory")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.Style")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.StateStyles")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.StateMachineMenu")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.StateMachineEditorTools")]
        [TestCase("YLFramework.StateMachine.Editor", "YLFramework.Editor.StateMachine.ListenerFile")]
        [TestCase("YLFramework.Localization.Editor", "YLFramework.Editor.Localization.LocalizationAuthoring")]
        [TestCase("YLFramework.Editor", "YLFramework.Editor.Audio.AudioClipLibraryMigration")]
        public void ImplementationTypesAreNotPartOfThePublicContract(string assemblyName, string fullName)
        {
            var type = System.Reflection.Assembly.Load(assemblyName).GetType(fullName);
            Assert.That(type, Is.Not.Null, fullName);
            Assert.That(type.IsVisible, Is.False, fullName);
            Assert.That(type.IsPublic, Is.False, fullName);
        }
    }
}

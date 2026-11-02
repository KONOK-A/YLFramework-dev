using System;
using System.Reflection;
using DG.Tweening;
using NUnit.Framework;
using YLFramework.Actions;
using YLFramework.Integrations.DOTween;

namespace YLFramework.Tests.Actions.DOTween
{
    public class DOTweenApiContractTests
    {
        [Test]
        public void OriginalPublicShapeAndPrivateConstructorArePreserved()
        {
            var type = typeof(DOTweenAction);
            Assert.That(typeof(IAction).IsAssignableFrom(type), Is.True);
            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(constructors[0].GetParameters(), Is.Empty);
            Assert.That(type.GetMethod("Allocate", new[] { typeof(Func<Tween>) }).ReturnType, Is.EqualTo(type));
            foreach (var name in new[] { "OnStart", "OnFinish", "Reset", "Deinit" })
            {
                Assert.That(type.GetMethod(name, Type.EmptyTypes).ReturnType, Is.EqualTo(typeof(void)));
            }

            Assert.That(type.GetMethod("OnExecute", new[] { typeof(float) }).ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(type.GetProperty("IsPaused").PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(type.GetProperty("IsDeinitialized").PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(type.GetProperty("ActionId").PropertyType, Is.EqualTo(typeof(ulong)));
            Assert.That(type.GetProperty("Status").PropertyType, Is.EqualTo(typeof(ActionStatus)));
            Assert.That(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Length, Is.EqualTo(4));
            var methods = Array.FindAll(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly), method => !method.IsSpecialName);
            Assert.That(methods.Length, Is.EqualTo(6));
            var toAction = typeof(ActionKitDOTweenExtension).GetMethod("ToAction", new[] { typeof(Tween) });
            var append = typeof(ActionKitDOTweenExtension).GetMethod("DOTween", new[] { typeof(ISequence), typeof(Func<Tween>) });
            Assert.That(toAction.ReturnType, Is.EqualTo(typeof(IAction)));
            Assert.That(append.ReturnType, Is.EqualTo(typeof(ISequence)));
            Assert.That(toAction.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false), Is.True);
            Assert.That(append.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false), Is.True);
        }
    }
}

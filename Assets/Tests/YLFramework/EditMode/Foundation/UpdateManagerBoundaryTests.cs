using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Events;

namespace YLFramework.Foundation.Tests
{
    public class UpdateManagerBoundaryTests
    {
        [Test]
        public void PublicUpdateHasOneNeutralAssemblyOwnerAndTokenApi()
        {
            var type = typeof(ApplicationTool).Assembly.GetType("YLFramework.Update.UpdateManager");
            Assert.That(type, Is.Not.Null);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Assert.That(assembly.GetType("YLFramework.Core.UpdateManager"), Is.Null);
            }

            foreach (var phase in new[] { "Update", "FixedUpdate", "LateUpdate" })
            {
                Assert.That(type.GetMethod("Add" + phase + "Event").ReturnType, Is.EqualTo(typeof(IUnregister)));
            }
        }

        [Test]
        public void RegistrationOutsidePlayAndCleanupNeverCreateHost()
        {
            var type = typeof(ApplicationTool).Assembly.GetType("YLFramework.Update.UpdateManager");
            Assert.That(type, Is.Not.Null);
            Assert.That(Application.isPlaying, Is.False);
            foreach (var phase in new[] { "Update", "FixedUpdate", "LateUpdate" })
            {
                var token = (IUnregister)type.GetMethod("Add" + phase + "Event").Invoke(null, new object[] { (Action)(() => Assert.Fail()) });
                Assert.That(token, Is.Not.Null);
                token.Unregister();
                token.Unregister();
                type.GetMethod("Remove" + phase + "Event").Invoke(null, new object[] { null });
            }
            Assert.That(UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
        }
    }
}

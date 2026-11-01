using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace YLFramework.Core.Tests
{
    public class CoreCoexistencePlayModeTests
    {
        [UnityTest]
        public IEnumerator RetiredLogUtilLeavesNoGlobalCallbacksOrFileSideEffects()
        {
            Assert.That(typeof(Module).Assembly.GetType("YLFramework.Core.LogUtil"), Is.Null);
            var oldDirectory = Path.Combine(Application.temporaryCachePath, "Logs");
            var previousLogs = Snapshot(oldDirectory);
            var ylDirectory = Path.Combine(Application.temporaryCachePath, "YLFramework", "Core", "Logs");
            var previousYLLogs = Snapshot(ylDirectory);
            var oldExists = Directory.Exists(oldDirectory);
            var ylExists = Directory.Exists(ylDirectory);
            var deadline = Time.realtimeSinceStartup + 0.1f;
            do
            {
                AssertNoRetiredCallbacks(typeof(Application));
#if UNITY_EDITOR
                AssertNoRetiredCallbacks(typeof(UnityEditor.EditorApplication));
#endif
                yield return null;
            } while (Time.realtimeSinceStartup < deadline);
            CollectionAssert.AreEquivalent(previousLogs, Snapshot(oldDirectory));
            CollectionAssert.AreEquivalent(previousYLLogs, Snapshot(ylDirectory));
            Assert.That(Directory.Exists(oldDirectory), Is.EqualTo(oldExists));
            Assert.That(Directory.Exists(ylDirectory), Is.EqualTo(ylExists));
        }

        private static void AssertNoRetiredCallbacks(Type publisher)
        {
            foreach (var field in publisher.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.GetValue(null) is Delegate callbacks)
                {
                    foreach (var callback in callbacks.GetInvocationList())
                    {
                        Assert.That(callback.Method.DeclaringType?.FullName, Is.Not.EqualTo("YLFramework.Core.LogUtil"), field.Name);
                    }
                }
            }
        }

        private static Dictionary<string, string> Snapshot(string directory)
        {
            var files = new Dictionary<string, string>();
            if (!Directory.Exists(directory))
            {
                return files;
            }

            using (var hash = SHA256.Create())
            {
                foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        files.Add(path, Convert.ToBase64String(hash.ComputeHash(stream)));
                    }
                }
            }

            return files;
        }
    }
}

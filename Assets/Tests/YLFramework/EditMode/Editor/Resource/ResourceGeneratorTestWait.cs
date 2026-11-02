using System.Collections;
using NUnit.Framework;
using UnityEditor;

namespace YLFramework.Tests.ResourceEditor
{
    internal static class ResourceGeneratorTestWait
    {
        internal static IEnumerator Clear(string operation)
        {
            double deadline = EditorApplication.timeSinceStartup + 90;
            int idleFrames = 0;
            do
            {
                yield return null;
                Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False,
                    operation + ": the fixture must not enter Play mode.");
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    string message = operation + ": Editor did not settle within 90 seconds. isCompiling=" + EditorApplication.isCompiling +
                        ", isUpdating=" + EditorApplication.isUpdating;
                    UnityEngine.Debug.Log(message);
                    Assert.Fail(message);
                }
                idleFrames = EditorApplication.isCompiling || EditorApplication.isUpdating ? 0 : idleFrames + 1;
            }
            while (idleFrames < 2);
        }
    }
}

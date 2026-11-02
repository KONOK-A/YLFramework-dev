using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Equipment
{
    public class EquipmentSettingsAdmissionTests
    {
        private static Type Helper => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Tests.EquipmentAcceptance.EquipmentSettings")).First(t => t != null);
        private static object Call(string name, params object[] args)
        {
            try
            {
                return Helper.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        [Test]
        public void NewSetterCannotAdoptAnUnownedAndroidChange()
        {
            const string Key = "YLFramework.Equipment.Acceptance.Settings";
            var android = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);
            Call("Capture", "Temp/EquipmentAcceptance/Admission-" + Guid.NewGuid().ToString("N"));
            try
            {
                PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android, android + ";EQ_SIMULATED_OTHER_OWNER");
                Assert.Throws<InvalidOperationException>(() => Call("EnableAcceptance"));
                Assert.That(PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android), Does.Contain("EQ_SIMULATED_OTHER_OWNER"));
            }
            finally
            {
                // 此同步用例仅撤销自己模拟的字段；失败实现也须恢复真实用户现场。
                PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android, android);
                var snapshot = JObject.Parse(SessionState.GetString(Key, ""));
                var player = (UnityEngine.Object)typeof(PlayerSettings).GetMethod("InternalGetPlayerSettingsObject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                using (var serialized = new SerializedObject(player))
                {
                    snapshot["expected"] = JArray.FromObject(Helper.GetMethod("ReadMaps", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { serialized }));
                }

                SessionState.SetString(Key, snapshot.ToString());
                Call("Restore");
            }
        }
    }
}

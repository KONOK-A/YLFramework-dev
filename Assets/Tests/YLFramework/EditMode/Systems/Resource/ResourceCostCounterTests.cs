using System;
using System.Collections.Generic;
using Win32Exception = System.ComponentModel.Win32Exception;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Resource.Addressables")]
    public class ResourceCostCounterTests
    {
        [Test]
        public void ProcessMemoryCountersRequirePositiveValuesAndNeverPublishUnsupportedZeros()
        {
            using var process = Process.GetCurrentProcess();
            var points = new List<JObject>();
            string unavailable = null;
            for (var i = 0; i < 3; i++)
            {
                long? workingSet = null;
                long? privateBytes = null;
                try
                {
                    process.Refresh();
                    var measuredWorkingSet = process.WorkingSet64;
                    var measuredPrivateBytes = process.PrivateMemorySize64;
                    if (measuredWorkingSet > 0)
                        workingSet = measuredWorkingSet;
                    if (measuredPrivateBytes > 0)
                        privateBytes = measuredPrivateBytes;
                    if (!workingSet.HasValue || !privateBytes.HasValue)
                        unavailable = "Runtime returned missing/non-positive process counters; these are not zero-memory measurements.";
                }
                catch (Exception exception) when (exception is Win32Exception || exception is PlatformNotSupportedException || exception is NotSupportedException)
                {
                    unavailable = exception.GetType().Name;
                }
                points.Add(new JObject
                {
                    ["WorkingSetBytes"] = workingSet.HasValue ? new JValue(workingSet.Value) : JValue.CreateNull(),
                    ["PrivateBytes"] = privateBytes.HasValue ? new JValue(privateBytes.Value) : JValue.CreateNull()
                });
            }

            var available = unavailable == null && points.All(point => point["WorkingSetBytes"].Type != JTokenType.Null && point["PrivateBytes"].Type != JTokenType.Null);
            var data = new JObject
            {
                ["ProcessCountersAvailable"] = available,
                ["ProcessCounterUnavailable"] = unavailable,
                ["PeakWorkingSetBytes"] = available ? new JValue(points.Max(point => (long)point["WorkingSetBytes"])) : JValue.CreateNull(),
                ["PeakPrivateBytes"] = available ? new JValue(points.Max(point => (long)point["PrivateBytes"])) : JValue.CreateNull(),
                ["Points"] = new JArray(points),
                ["Note"] = "Process samples only, not native allocation counts or instantaneous peaks. No GC.Collect or cache flush."
            };
            TestContext.WriteLine(data.ToString());
            if ((bool)data["ProcessCountersAvailable"])
            {
                Assert.That((long)data["PeakWorkingSetBytes"], Is.GreaterThan(0), "A successful API call returning zero is not a valid working-set measurement.");
                Assert.That((long)data["PeakPrivateBytes"], Is.GreaterThan(0));
            }
            else
            {
                Assert.That(data["PeakWorkingSetBytes"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That(data["PeakPrivateBytes"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That((string)data["ProcessCounterUnavailable"], Is.Not.Empty);
            }
            foreach (var point in data["Points"])
            {
                foreach (var counter in new[] { "WorkingSetBytes", "PrivateBytes" })
                {
                    Assert.That(point[counter].Type == JTokenType.Null || (long)point[counter] > 0, Is.True);
                }
            }
        }
    }
}

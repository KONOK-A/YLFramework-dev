using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceCostCounterTests
    {
        [Test]
        public void ProcessMemoryCountersRequirePositiveValuesAndNeverPublishUnsupportedZeros()
        {
            var type = typeof(ResourceCostBaselineTests).GetNestedType("MemorySampler", BindingFlags.NonPublic);
            using var sampler = (IDisposable)Activator.CreateInstance(type, true);
            var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
            for (var i = 0; i < 3; i++)
            {
                tick.Invoke(sampler, new object[] { null });
            }

            var data = JObject.FromObject(type.GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(sampler, null));
            ResourceCostEvidence.Write("process-memory-capability", data);
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

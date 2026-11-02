using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YLFramework.Mission;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class MissionParameterCopyTests
    {
        [Test]
        public void ClipboardRoundTripRetainsCustomDataButCopyDropsRuntimeValueAndCallbacks()
        {
            var source = new MissionParameter { name = "parameter", type = MissionParamType.Int };
            typeof(MissionParameter).GetField("customData", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(source, new List<string> { "category" });
            source.Value = 9;
            source.ValueChanged = _ => throw new InvalidOperationException("source callback must not be copied");
            var pasted = JsonConvert.DeserializeObject<List<MissionParameter>>(JsonConvert.SerializeObject(new[] { source }))[0];
            var copy = (MissionParameter)typeof(MissionParameter).GetMethod("CloneConfiguration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(pasted, null);
            Assert.That(copy.ContainsCustomData("category"), Is.True);
            Assert.That(copy.Value, Is.Zero);
            Assert.That(copy.ValueChanged, Is.Null);
            Assert.That(source.Value, Is.EqualTo(9));
        }
    }
}

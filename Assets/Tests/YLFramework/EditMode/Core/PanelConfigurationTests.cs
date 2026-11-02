using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace YLFramework.Core.Tests
{
    public sealed class PanelConfigurationTests
    {
        [Test]
        public void ConfigRetainsSerializableSevenFieldValueLayoutAndNaturalBooleans()
        {
            Assert.That(typeof(PanelConfig).IsValueType, Is.True);
            Assert.That(typeof(PanelConfig).IsDefined(typeof(SerializableAttribute), false), Is.True);
            var fields = typeof(PanelConfig).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(fields.Select(field => field.Name), Is.EquivalentTo(new[]
                { "layer", "priority", "modal", "coversRoot", "participatesInTop", "instanceMode", "cacheOnClose" }));
            foreach (var field in fields.Where(field => field.Name == "modal" || field.Name == "coversRoot" ||
                field.Name == "participatesInTop" || field.Name == "cacheOnClose"))
            {
                Assert.That(field.FieldType, Is.EqualTo(typeof(bool)));
            }
        }

        [Test]
        public void DefaultAndExplicitZeroOrFalseRemainDistinct()
        {
            var settings = PanelConfig.Default;
            Assert.That(settings.participatesInTop && settings.cacheOnClose, Is.True);
            Assert.That(settings.instanceMode, Is.EqualTo(PanelInstanceMode.Single));
            settings = new PanelOverrides { Priority = 8, Modal = true }.ApplyTo(settings);
            settings = new PanelOverrides { Priority = 0, Modal = false, ParticipatesInTop = false, CacheOnClose = false }.ApplyTo(settings);
            Assert.That(settings.priority, Is.Zero);
            Assert.That(settings.modal || settings.participatesInTop || settings.cacheOnClose, Is.False);
            Assert.That(default(PanelOverrides).ApplyTo(PanelConfig.Default).cacheOnClose, Is.True);
        }

        [Test]
        public void PrefabThenRuleThenRequestMergeAllSevenFields()
        {
            var prefab = new PanelOverrides
            {
                Layer = 2,
                Priority = 7,
                Modal = true,
                CoversRoot = true,
                ParticipatesInTop = false,
                InstanceMode = PanelInstanceMode.Multiple,
                CacheOnClose = false
            }.ApplyTo(PanelConfig.Default);
            var rules = PanelRuleSnapshot.Capture(new[] { new PanelRule("ui/Window.prefab", new PanelOverrides { Layer = 4, Priority = 5, Modal = false, CacheOnClose = true }) });
            var result = rules.Resolve("ui/Window.prefab", null).ApplyTo(prefab);
            result = new PanelOverrides { Priority = 0, CoversRoot = false }.ApplyTo(result);
            Assert.That(result.layer, Is.EqualTo(4));
            Assert.That(result.priority, Is.Zero);
            Assert.That(result.modal || result.coversRoot || result.participatesInTop, Is.False);
            Assert.That(result.instanceMode, Is.EqualTo(PanelInstanceMode.Multiple));
            Assert.That(result.cacheOnClose, Is.True);
        }

        [Test]
        public void SnapshotCopiesRowsAndUsesOrdinalFullAddresses()
        {
            var rows = new List<PanelRule> { new PanelRule("ui/Window.prefab", new PanelOverrides { Priority = 3 }) };
            var snapshot = PanelRuleSnapshot.Capture(rows);
            rows[0] = new PanelRule("ui/Window.prefab", new PanelOverrides { Priority = 9 });
            rows.Clear();
            Assert.That(snapshot.Resolve("ui/Window.prefab", null).Priority, Is.EqualTo(3));
            Assert.That(snapshot.Resolve("UI/Window.prefab", null).Priority, Is.Null);
            Assert.That(snapshot.Resolve("Window.prefab", null).Priority, Is.Null);
            Assert.That(PanelRuleSnapshot.Capture(null).Resolve("ui/Window.prefab", null).Priority, Is.Null);
        }

        [Test]
        public void OrderingUsesLayerThenModalThenPriorityThenAcceptedSequenceWithoutOverflow()
        {
            var low = new PanelOverrides { Priority = int.MaxValue }.ApplyTo(PanelConfig.Default);
            var modal = new PanelOverrides { Priority = int.MinValue, Modal = true }.ApplyTo(PanelConfig.Default);
            Assert.That(PanelConfig.Compare(low, long.MaxValue, modal, long.MinValue), Is.LessThan(0));
            var higherLayer = new PanelOverrides { Layer = 1, Priority = int.MinValue }.ApplyTo(PanelConfig.Default);
            Assert.That(PanelConfig.Compare(modal, long.MaxValue, higherLayer, long.MinValue), Is.LessThan(0));
            Assert.That(PanelConfig.Compare(low, 1, low, 2), Is.LessThan(0));
            Assert.That(PanelConfig.Compare(low, long.MaxValue, low, long.MaxValue), Is.Zero);
            Assert.That(PanelConfig.Compare(low, 1, new PanelOverrides { Priority = int.MinValue }.ApplyTo(low), 2), Is.GreaterThan(0));
        }

        [Test]
        public void InvalidRulesAndDisplayPolicyChangesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => PanelRuleSnapshot.Capture(new[] { default(PanelRule) }));
            Assert.Throws<ArgumentException>(() => PanelRuleSnapshot.Capture(new[] { new PanelRule(" ", default) }));
            Assert.Throws<ArgumentException>(() => PanelRuleSnapshot.Capture(new[] { new PanelRule("a", default), new PanelRule("a", default) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => PanelRuleSnapshot.Capture(new[] { new PanelRule("a", new PanelOverrides { Layer = -1 }) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PanelOverrides { InstanceMode = (PanelInstanceMode)42 }.ApplyTo(PanelConfig.Default));
            Assert.Throws<ArgumentException>(() => new PanelOverrides { CacheOnClose = false }.ValidateDisplay());
            Assert.Throws<ArgumentException>(() => new PanelOverrides { InstanceMode = PanelInstanceMode.Single }.ValidateDisplay());
        }
    }
}

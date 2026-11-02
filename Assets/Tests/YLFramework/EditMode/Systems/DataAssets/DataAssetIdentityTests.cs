using System;
using NUnit.Framework;
using YLFramework.DataAssets;

namespace YLFramework.Tests.DataAssets
{
    public class DataAssetIdentityTests
    {
        [Test]
        public void EmptyAndMissingRowsHaveNoRange()
        {
            var index = new DataAssetIdIndex(new int?[] { null, null }, "empty");
            Assert.That(index.TryGetRange(out var min, out var max), Is.False);
            Assert.That(min, Is.Zero);
            Assert.That(max, Is.Zero);
            Assert.That(index.NextId(), Is.EqualTo(1));
            Assert.That(index.TryGetRow(0, out _), Is.False);
        }

        [Test]
        public void RangeAndLookupPreserveOriginalRowIndices()
        {
            var index = new DataAssetIdIndex(new int?[] { 9, null, -7, 3 }, "items");
            Assert.That(index.TryGetRange(out var min, out var max), Is.True);
            Assert.That(min, Is.EqualTo(-7));
            Assert.That(max, Is.EqualTo(9));
            Assert.That(index.TryGetRow(3, out var row), Is.True);
            Assert.That(row, Is.EqualTo(3));
            Assert.That(index.NextId(), Is.EqualTo(10));
        }

        [Test]
        public void DuplicateDiagnosticNamesActualIdAndBothRows()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                new DataAssetIdIndex(new int?[] { 7, null, 7 }, "potions"));
            Assert.That(error.Message, Does.Contain("potions").And.Contain("7").And.Contain("0").And.Contain("2"));
        }

        [Test]
        public void ExhaustedMaximumUsesFirstAvailablePositiveIdWithoutOverflow()
        {
            var index = new DataAssetIdIndex(new int?[] { int.MaxValue, 1, 3 }, "items");
            Assert.That(index.NextId(), Is.EqualTo(2));
        }

        [Test]
        public void NegativeOnlyIdsStillGeneratePositiveIdentity()
        {
            var index = new DataAssetIdIndex(new int?[] { -9, -1, 0 }, "items");
            Assert.That(index.NextId(), Is.EqualTo(1));
        }

        [Test]
        public void SameRootHasIndependentRegistrationsAndLastReleaseClearsCache()
        {
            var removed = 0;
            var registry = new DataAssetRegistry<object>(_ => removed++);
            var value = new object();
            using var first = registry.Register("items", 41, value);
            using var second = registry.Register("items", 41, value);
            first.Dispose();
            Assert.That(registry.Get("items"), Is.SameAs(value));
            Assert.That(removed, Is.Zero);
            second.Dispose();
            second.Dispose();
            Assert.That(registry.Get(41), Is.Null);
            Assert.That(removed, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateNameCannotSilentlyReplaceAnotherRoot()
        {
            var registry = new DataAssetRegistry<object>();
            var original = new object();
            using var registration = registry.Register("items", 41, original);
            Assert.Throws<InvalidOperationException>(() => registry.Register("items", 41, new object()));
            Assert.That(registry.Get("items"), Is.SameAs(original));
        }

        [Test]
        public void HashCollisionIsRejectedAndDoesNotAliasStringLookup()
        {
            var registry = new DataAssetRegistry<object>();
            var original = new object();
            using var registration = registry.Register("first", 41, original);
            var error = Assert.Throws<InvalidOperationException>(() => registry.Register("second", 41, new object()));
            Assert.That(error.Message, Does.Contain("first").And.Contain("second"));
            Assert.That(registry.Get("second"), Is.Null);
            Assert.That(registry.Get(41), Is.SameAs(original));
        }

        [Test]
        public void NamesUseOrdinalIdentityAndRenamingRequiresRelease()
        {
            var registry = new DataAssetRegistry<object>();
            var value = new object();
            using var first = registry.Register("Items", 41, value);
            Assert.That(registry.Get("items"), Is.Null);
            Assert.Throws<InvalidOperationException>(() => registry.Register("renamed", 42, value));
            first.Dispose();
            using var renamed = registry.Register("renamed", 42, value);
            Assert.That(registry.Get("Items"), Is.Null);
            Assert.That(registry.Get("renamed"), Is.SameAs(value));
        }

        [Test]
        public void OldRegistrationCannotRemoveReplacementAfterClear()
        {
            var registry = new DataAssetRegistry<object>();
            var old = registry.Register("items", 41, new object());
            registry.Clear();
            var value = new object();
            using var replacement = registry.Register("items", 41, value);
            old.Dispose();
            Assert.That(registry.Get("items"), Is.SameAs(value));
        }
    }
}

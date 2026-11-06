#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotPackageBoundaryTests
    {
        [Test]
        public void OriginalTypesHaveOnePackageOwner()
        {
            Assert.That(typeof(ReddotManager).FullName, Is.EqualTo("YLFramework.RedDots.ReddotManager"));
            Assert.That(typeof(Reddot).FullName, Is.EqualTo("YLFramework.RedDots.Reddot"));
            Assert.That(typeof(ReddotManager).Assembly.GetName().Name, Is.EqualTo("YLFramework.RedDots"));
            Assert.That(typeof(Reddot).Assembly, Is.SameAs(typeof(ReddotManager).Assembly));
        }

        [Test]
        public void OriginalComponentAndManagerGuidsMoveWithoutRegeneratingIdentity()
        {
            const string Root = "Packages/com.kano.ylframework.reddot/Runtime/Systems/Reddot/";
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "Reddot.cs"), Is.EqualTo("3ff4191f5232945449bde5c92a1b497d"));
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "ReddotManager.cs"), Is.EqualTo("13f96a08d4b3e054986887b7cb3422b4"));
            Assert.That(AssetDatabase.AssetPathToGUID("Packages/com.kano.ylframework/Runtime/Systems/Reddot/Reddot.cs"), Is.Empty);
        }
    }
}
#endif

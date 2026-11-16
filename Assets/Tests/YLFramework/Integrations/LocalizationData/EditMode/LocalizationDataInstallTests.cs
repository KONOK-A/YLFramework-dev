using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using YLFramework.Localization;

namespace YLFramework.Tests.Localization.Data
{
    public class LocalizationDataInstallTests
    {
        [Test]
        public void DataAssetsOnlyEnablesItsAdapterWithoutInstallingExcel()
        {
            var packages = PackageInfo.GetAllRegisteredPackages().Select(package => package.name).ToArray();
            Assert.That(packages, Does.Contain("com.kano.ylframework.data-assets"));
            Assert.That(packages, Does.Not.Contain("com.kano.ylframework.excel"));
            Assert.That(Type.GetType("YLFramework.Editor.Localization.DataAssets.LocalizationDataAssetsCommands, YLFramework.Localization.DataAssets.Editor"), Is.Not.Null);
            Assert.That(Type.GetType("YLFramework.Editor.Localization.Excel.LocalizationExcelCommands, YLFramework.Localization.Excel.Editor"), Is.Null);
            Assert.That(typeof(LocalizationSession).Assembly.GetReferencedAssemblies().Select(name => name.Name), Does.Not.Contain("YLFramework.DataAssets"));
        }
    }
}

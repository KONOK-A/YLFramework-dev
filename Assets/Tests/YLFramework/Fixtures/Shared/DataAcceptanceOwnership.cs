namespace YLFramework.Tests.DataAssets
{
    public static class DataAcceptanceOwnership
    {
        public static void Own(string path)
        {
            string output = System.Environment.GetEnvironmentVariable("YL_DATA_UNITY_OUTPUT");
            if (string.IsNullOrEmpty(output) || !System.IO.Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(output)).Replace('\\', '/').StartsWith("G:/Unity/MB26/", System.StringComparison.Ordinal))
            {
                return;
            }

            var type = System.Type.GetType("YLFramework.Tests.DataBatch.DataMainAcceptance, YLFramework.Tests.Tools.Editor", true);
            type.GetMethod("OwnTestPath").Invoke(null, new object[] { path });
        }
    }
}

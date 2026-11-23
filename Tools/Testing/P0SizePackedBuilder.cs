#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine.Build.Pipeline;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.AddressableAssets.ResourceLocators;
using YLFramework.Acceptance;

namespace YLFramework.Testing
{
    // Only this experiment has a fixed-length sample envelope. The production exporter is not implemented here.
    public sealed class P0SizePackedBuilder : BuildScriptPackedMode
    {
        public const int FixtureCipherOverhead = 12 + 16;
        public AddressableAssetGroup ProtectedGroup { get; set; }
        public bool SeparateMount { get; set; }
        public long PackedPlainBytes { get; private set; }
        private AddressableAssetsBuildContext _context;

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput input, AddressableAssetsBuildContext context)
        {
            _context = context;
            try { return base.DoBuild<TResult>(input, context); }
            finally { _context = null; }
        }

        protected override string ConstructAssetBundleName(AddressableAssetGroup group, BundledAssetGroupSchema schema, BundleDetails info, string assetBundleName)
        {
            if (group == ProtectedGroup)
            {
                string name = Path.GetFileNameWithoutExtension(info.FileName);
                var transfer = _context.locations.Single(entry => entry.ResourceType == typeof(IAssetBundleResource) &&
                    entry.Data is AssetBundleRequestOptions data && data.BundleName == name);
                var options = (AssetBundleRequestOptions)transfer.Data;
                if (PackedPlainBytes != 0 || options.BundleSize <= 0)
                    throw new InvalidOperationException("The P0-A hook expects one real protected bundle after official packing.");
                PackedPlainBytes = options.BundleSize;
                if (!SeparateMount) options.BundleSize = checked(PackedPlainBytes + FixtureCipherOverhead);
                else
                {
                    object transferKey = transfer.Keys[0];
                    string mountKey = P0HeldBundleProvider.MountPrefix + name;
                    var owners = _context.locations.Where(entry => entry.Dependencies.Contains(transferKey)).ToArray();
                    if (owners.Length != 2 || owners.Any(entry => entry.ResourceType != typeof(UnityEngine.TextAsset) ||
                        !(entry.Keys.Contains(ResourceAddressablesPlayerProbe.SizeProtectedA) || entry.Keys.Contains(ResourceAddressablesPlayerProbe.SizeProtectedB))))
                        throw new InvalidOperationException("P0-B permits only the two audited protected fixture assets.");
                    foreach (var owner in owners) owner.Dependencies.Add(mountKey);
                    _context.locations.Add(new ContentCatalogDataEntry(typeof(P0HeldMountedBundle), mountKey, transfer.Provider, new[] { mountKey }));
                }
            }
            return base.ConstructAssetBundleName(group, schema, info, assetBundleName);
        }
    }
}
#endif

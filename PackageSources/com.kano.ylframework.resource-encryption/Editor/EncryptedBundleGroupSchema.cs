using System;
using System.ComponentModel;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace YLFramework.Resource.Encryption.Editor
{
    /// <summary>Controls which remote group is protected. This asset contains identifiers only, never key material.</summary>
    [DisplayName("Encrypted Bundle Delivery")]
    public sealed class EncryptedBundleGroupSchema : AddressableAssetGroupSchema
    {
        [SerializeField] private string _keyId = "";
        [SerializeField] private string[] _publicDependencyGuids = Array.Empty<string>();

        internal string KeyId => _keyId;
        internal string[] PublicDependencyGuids => _publicDependencyGuids;
    }
}

using System;
using System.ComponentModel;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace YLFramework.Resource.Encryption.Editor
{
    /// <summary>Selects encrypted delivery for a group. Stores identifiers only, never key material.</summary>
    [DisplayName("Encrypted Bundle Delivery")]
    public sealed class EncryptedBundleGroupSchema : AddressableAssetGroupSchema
    {
        [SerializeField] private string _keyId = "";
        [SerializeField] private string[] _publicDependencyGuids = Array.Empty<string>();
        [SerializeField] private EncryptedBundleDeliveryMode _deliveryMode = EncryptedBundleDeliveryMode.RemoteHttps;

        /// <summary>How this group's ciphertext is delivered in the fixed release.</summary>
        public EncryptedBundleDeliveryMode DeliveryMode => _deliveryMode;
        internal string KeyId => _keyId;
        internal string[] PublicDependencyGuids => _publicDependencyGuids;
    }
}

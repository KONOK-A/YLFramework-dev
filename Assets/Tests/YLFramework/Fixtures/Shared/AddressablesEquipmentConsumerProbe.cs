#if YL_GAMEPLAY_PACKAGE
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Tests.Extensions.Domain
{
    public sealed class AddressablesEquipmentConsumerProbe : MonoBehaviour, IEquipment
    {
        public IEquippable Equipper { get; set; }
        public EquipmentConfigData EquipmentData { get; set; }
        public int Loads;
        public int Unloads;
        public void OnLoaded() => Loads++;
        public void OnUnloaded() => Unloads++;
        public void OnEquip() { }
        public void OnUnequip() { }
    }
}
#endif

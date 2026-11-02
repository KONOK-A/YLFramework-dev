using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Tests.Extensions.Equipment
{
    public sealed class EquipmentResourceProbe : MonoBehaviour, IEquipment, IEquippable
    {
        public static bool FailLoad;
        public static EquipmentResourceProbe LastLoaded;
        public IEquippable Equipper
        {
            get; set;
        }
        public EquipmentConfigData EquipmentData
        {
            get; set;
        }
        public List<IEquipment> Equipments { get; } = new();
        [global::UnityEngine.Serialization.FormerlySerializedAs("Loads")]
        public int loads;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Unloads")]
        public int unloads;
        public bool CanEquip(IEquipment equipment) => true;
        public void OnLoaded()
        {
            loads++;
            LastLoaded = this;
            if (!EquipmentData)
            {
                throw new InvalidOperationException("Definition must precede OnLoaded.");
            }

            if (FailLoad)
            {
                throw new InvalidOperationException("probe-load-fail");
            }
        }
        public void OnUnloaded()
        {
            unloads++;
        }
        public void OnEquip()
        {
        }
        public void OnUnequip()
        {
        }
    }
}

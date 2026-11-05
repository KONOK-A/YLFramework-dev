using System;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>由 Undo 保存期望删除状态，并保留独立装备文件的恢复资料；不写成项目资产。</summary>
    internal sealed class EquipmentDeletionRecord : ScriptableObject
    {
        /// <summary>解除或恢复条目引用的所属配表。</summary>
        public EquipmentsConfig root;
        /// <summary>原装备资产文件路径。</summary>
        public string path;
        /// <summary>需保持不变的原资产 GUID。</summary>
        public string guid;
        /// <summary>删除前的内存字段 JSON，可能包含尚未保存的编辑。</summary>
        public string memory;
        /// <summary>删除前磁盘上的资产文件字节。</summary>
        public byte[] assetBytes;
        /// <summary>删除前磁盘上的 meta 文件字节。</summary>
        public byte[] metaBytes;
        /// <summary>配表中引用该条目的原始槽位。</summary>
        public int[] indices;
        /// <summary>参与 Undo 的期望删除状态，true 表示应删除文件。</summary>
        public bool deleted;
        /// <summary>条目删除前是否存在未保存的内存修改。</summary>
        public bool wasDirty;
        /// <summary>上次同步尝试所对应的删除状态，不参与 Undo 序列化。</summary>
        [NonSerialized] public bool IsApplied;
    }
}

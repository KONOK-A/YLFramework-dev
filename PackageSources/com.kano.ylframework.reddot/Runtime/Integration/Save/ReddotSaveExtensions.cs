using System;
using System.Threading;
using System.Threading.Tasks;
using YLFramework.Save;

namespace YLFramework.RedDots
{
    /// <summary>Optional snapshot-only IO on an existing caller-owned SaveStore.</summary>
    /// <remarks>
    /// These operations never access read state or Manager, choose a slot, recover files or create a store.
    /// DTO validation is pure; callers explicitly Import a successful result on the main thread.
    /// </remarks>
    public static class ReddotSaveExtensions
    {
        /// <summary>Read and validate the shared DTO, preserving Missing, backup provenance and cache identity.</summary>
        /// <remarks>Malformed snapshots, IO failures and cancellation propagate without repair/default writes.</remarks>
        public static async Task<SaveReadResult<ReddotReadSnapshot>> ReadReddotSnapshotAsync(this SaveStore store,
            string name, int id = 0, CancellationToken cancellationToken = default)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            var result = await store.ReadObjectAsync<ReddotReadSnapshot>(name, id, cancellationToken).ConfigureAwait(false);
            if (result.Status != SaveReadStatus.Missing)
            {
                result.Value.Validate();
            }
            return result;
        }

        /// <summary>Validate and save the same DTO with SaveStore's original name, slot and cancellation semantics.</summary>
        /// <remarks>Serialization captures the DTO on the calling thread; no implicit Export or Import is performed.</remarks>
        public static Task SaveReddotSnapshotAsync(this SaveStore store, ReddotReadSnapshot value,
            string name, int id = 0, CancellationToken cancellationToken = default)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            value.Validate();
            return store.SaveObjectAsync(value, name, id, cancellationToken);
        }
    }
}

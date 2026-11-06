using System;
using System.Collections.Generic;
using System.IO;

namespace YLFramework.RedDots
{
    /// <summary>Plain snapshot DTO containing only explicitly confirmed string key/version sets.</summary>
    /// <remarks>Missing fields remain invalid. This DTO has no dependency on a JSON library or the Manager.</remarks>
    [Serializable]
    public sealed class ReddotReadSnapshot
    {
        /// <summary>Supported schema is 1; default 0 deliberately rejects a missing schema field.</summary>
        public int schemaVersion;
        /// <summary>Explicit account/slot namespace, compared with Ordinal semantics on Import.</summary>
        public string readNamespace;
        /// <summary>One record per confirmed key; an empty array represents an empty confirmed set.</summary>
        public ReddotReadRecord[] records;

        /// <summary>Pure validation of the complete DTO; never imports state, accesses Unity or repairs data.</summary>
        /// <exception cref="InvalidDataException">Unsupported schema, missing fields or duplicate records.</exception>
        public void Validate() => CopyRecords();

        internal Dictionary<string, HashSet<string>> CopyRecords()
        {
            if (schemaVersion != 1)
            {
                throw new InvalidDataException("Unsupported Reddot read snapshot schema: " + schemaVersion);
            }
            if (string.IsNullOrWhiteSpace(readNamespace) || records == null)
            {
                throw new InvalidDataException("Reddot read snapshot requires a namespace and records.");
            }
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.key) || record.versions == null ||
                    record.versions.Length == 0 || result.ContainsKey(record.key))
                {
                    throw new InvalidDataException("Invalid or duplicate Reddot read record.");
                }
                var versions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var version in record.versions)
                {
                    if (string.IsNullOrWhiteSpace(version) || !versions.Add(version))
                    {
                        throw new InvalidDataException("Invalid or duplicate Reddot read version.");
                    }
                }
                result.Add(record.key, versions);
            }
            return result;
        }
    }

    /// <summary>One stable item key and its independent, unordered confirmed content versions.</summary>
    [Serializable]
    public sealed class ReddotReadRecord
    {
        /// <summary>Required nonblank key; case and surrounding nonblank whitespace are significant.</summary>
        public string key;
        /// <summary>Nonempty set of required nonblank versions; no numeric or latest-version ordering.</summary>
        public string[] versions;
    }
}

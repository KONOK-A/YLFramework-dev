using System;
using System.IO;
using System.Text;

namespace YLFramework.Integrations.HybridCLR
{
    /// <summary>仅从有界 PE/CLR 字节读取 Module.Mvid，不加载程序集，也不依赖运行时 dnlib。</summary>
    internal static class HybridCLRMetadata
    {
        // 只读 ECMA-335 Module.Mvid；IL2CPP 没有实现 CLR Module GUID，不能用空 Guid 冒充核验。
        internal static Guid ReadMvid(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.LongLength > HybridCLRManifest.MaximumPayloadBytes)
            {
                throw new InvalidDataException("Missing or oversized managed PE input.");
            }

            void Need(int offset, int length)
            {
                if (offset < 0 || length < 0 || length > bytes.Length || offset > bytes.Length - length)
                {
                    throw new InvalidDataException("Managed PE metadata offset is outside its payload.");
                }
            }
            int Offset(uint value)
            {
                if (value > int.MaxValue)
                {
                    throw new InvalidDataException("Managed PE offset overflow.");
                }

                return (int)value;
            }
            int Add(int left, int right)
            {
                if (left < 0 || right < 0 || left > int.MaxValue - right)
                {
                    throw new InvalidDataException("Managed PE offset overflow.");
                }

                return left + right;
            }
            ushort U16(int offset)
            {
                Need(offset, 2);
                return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
            }
            uint U32(int offset)
            {
                Need(offset, 4);
                return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
            }
            void Inside(int start, int size, int offset, int length)
            {
                Need(offset, length);
                if (offset < start || length > size || offset - start > size - length)
                {
                    throw new InvalidDataException("Managed metadata stream escaped its declared range.");
                }
            }
            if (U16(0) != 0x5a4d)
            {
                throw new InvalidDataException("Not a managed PE file.");
            }

            var pe = Offset(U32(0x3c));
            if (U32(pe) != 0x00004550)
            {
                throw new InvalidDataException("Invalid PE signature.");
            }

            var sections = U16(Add(pe, 6));
            var optionalSize = U16(Add(pe, 20));
            if (sections == 0 || sections > 96)
            {
                throw new InvalidDataException("Unsupported PE section count.");
            }

            var optional = Add(pe, 24);
            Need(optional, optionalSize);
            var magic = U16(optional);
            var directories = magic == 0x10b ? 96 : magic == 0x20b ? 112 : throw new InvalidDataException("Unsupported PE optional header.");
            if (optionalSize < directories + 15 * 8 || U32(Add(optional, directories - 4)) < 15)
            {
                throw new InvalidDataException("The PE has no CLR directory.");
            }

            var sectionStart = Add(optional, optionalSize);
            Need(sectionStart, sections * 40);
            int Rva(uint rva, int size)
            {
                for (var index = 0; index < sections; index++)
                {
                    var section = sectionStart + index * 40;
                    var address = U32(section + 12);
                    var rawSize = U32(section + 16);
                    if (rva < address || rva - address >= rawSize || size < 0 || (uint)size > rawSize - (rva - address))
                    {
                        continue;
                    }

                    var raw = U32(section + 20);
                    var delta = rva - address;
                    if ((ulong)raw + delta > int.MaxValue)
                    {
                        throw new InvalidDataException("PE RVA overflow.");
                    }

                    var result = (int)(raw + delta);
                    Need(result, size);
                    return result;
                }
                throw new InvalidDataException("CLR metadata RVA is not backed by file bytes.");
            }
            var cli = Rva(U32(Add(optional, directories + 14 * 8)), 24);
            if (U32(cli) < 24)
            {
                throw new InvalidDataException("Invalid CLR header.");
            }

            var metadataSize = Offset(U32(cli + 12));
            var metadata = Rva(U32(cli + 8), metadataSize);
            Inside(metadata, metadataSize, metadata, 16);
            if (U32(metadata) != 0x424a5342)
            {
                throw new InvalidDataException("Invalid CLR metadata signature.");
            }

            var versionLength = Offset(U32(metadata + 12));
            var cursor = Add(metadata, Add(16, versionLength));
            Inside(metadata, metadataSize, metadata + 16, versionLength);
            cursor = Add(cursor, (4 - cursor % 4) % 4);
            Inside(metadata, metadataSize, cursor, 4);
            var streamCount = U16(cursor + 2);
            cursor += 4;
            if (streamCount == 0 || streamCount > 64)
            {
                throw new InvalidDataException("Unsupported metadata stream count.");
            }

            var tables = -1;
            var tableSize = 0;
            var guids = -1;
            var guidSize = 0;
            for (var stream = 0; stream < streamCount; stream++)
            {
                Inside(metadata, metadataSize, cursor, 8);
                var offset = Offset(U32(cursor));
                var size = Offset(U32(cursor + 4));
                cursor += 8;
                var nameStart = cursor;
                while (true)
                {
                    Inside(metadata, metadataSize, cursor, 1);
                    if (bytes[cursor++] == 0)
                    {
                        break;
                    }

                    if (cursor - nameStart > 32)
                    {
                        throw new InvalidDataException("Invalid metadata stream name.");
                    }
                }
                var name = Encoding.ASCII.GetString(bytes, nameStart, cursor - nameStart - 1);
                cursor = Add(cursor, (4 - cursor % 4) % 4);
                var start = Add(metadata, offset);
                Inside(metadata, metadataSize, start, size);
                if (name == "#GUID")
                {
                    if (guids >= 0)
                    {
                        throw new InvalidDataException("Duplicate GUID heap.");
                    }
                    guids = start;
                    guidSize = size;
                }
                if (name == "#~" || name == "#-")
                {
                    if (tables >= 0)
                    {
                        throw new InvalidDataException("Duplicate metadata tables.");
                    }
                    tables = start;
                    tableSize = size;
                }
            }
            if (tables < 0 || guids < 0)
            {
                throw new InvalidDataException("Missing Module table or GUID heap.");
            }

            Inside(tables, tableSize, tables, 24);
            var heaps = bytes[tables + 6];
            var valid = U32(tables + 8) | (ulong)U32(tables + 12) << 32;
            if ((valid & 1) == 0)
            {
                throw new InvalidDataException("The PE is not a manifest module.");
            }

            cursor = tables + 24;
            for (var table = 0; table < 64; table++)
            {
                if ((valid & 1UL << table) == 0)
                {
                    continue;
                }

                Inside(tables, tableSize, cursor, 4);
                if (table == 0 && U32(cursor) != 1)
                {
                    throw new InvalidDataException("A single manifest Module row is required.");
                }

                cursor += 4;
            }
            if ((heaps & 0x40) != 0)
            {
                Inside(tables, tableSize, cursor, 4);
                cursor += 4;
            }
            var stringIndex = (heaps & 1) == 0 ? 2 : 4;
            var guidIndex = (heaps & 2) == 0 ? 2 : 4;
            Inside(tables, tableSize, cursor, 2 + stringIndex + guidIndex * 3);
            var mvidIndex = guidIndex == 2 ? U16(cursor + 2 + stringIndex) : U32(cursor + 2 + stringIndex);
            if (mvidIndex == 0 || (ulong)(mvidIndex - 1) * 16 + 16 > (ulong)guidSize)
            {
                throw new InvalidDataException("MVID is outside the GUID heap.");
            }

            var data = new byte[16];
            Buffer.BlockCopy(bytes, guids + (int)(mvidIndex - 1) * 16, data, 0, 16);
            var mvid = new Guid(data);
            if (mvid == Guid.Empty)
            {
                throw new InvalidDataException("A nonempty module MVID is required.");
            }

            return mvid;
        }
    }
}

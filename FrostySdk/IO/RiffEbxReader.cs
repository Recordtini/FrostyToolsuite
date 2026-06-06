using FrostySdk.Attributes;
using FrostySdk.Ebx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FrostySdk.Ebx
{
    public class RiffEbxAsset
    {
        [Category("RIFF EBX")]
        [IsReadOnly]
        public CString Name { get; set; }

        [Category("RIFF EBX")]
        [IsReadOnly]
        public Guid TypeGuid { get; set; }

        [Category("RIFF EBX")]
        [IsReadOnly]
        public uint TypeSignature { get; set; }

        [Category("RIFF EBX")]
        [IsReadOnly]
        public List<ResourceRef> ResourceReferences { get; } = new List<ResourceRef>();

        [Category("RIFF EBX")]
        [IsReadOnly]
        public List<Guid> Dependencies { get; } = new List<Guid>();

        [Category("Annotations")]
        [DisplayName("Guid")]
        [IsReadOnly]
        public AssetClassGuid __InstanceGuid { get; set; }

        public AssetClassGuid GetInstanceGuid() => __InstanceGuid;
        public void SetInstanceGuid(AssetClassGuid guid) => __InstanceGuid = guid;
    }
}

namespace FrostySdk.IO
{
    public class RiffEbxReader : EbxReader
    {
        private const uint RiffMagic = 0x46464952;
        private const uint EbxMagic = 0x00584245;
        private const uint EbxdMagic = 0x44584245;
        private const uint EfixMagic = 0x58494645;
        private const uint EbxxMagic = 0x58584245;

        private sealed class RiffArrayInfo
        {
            public uint Offset;
            public int Count;
            public uint NameHash;
            public ushort Flags;
            public ushort TypeRef;
        }

        private long payloadOffset;
        private readonly List<Guid> typeGuids = new List<Guid>();
        private readonly List<uint> typeSignatures = new List<uint>();
        private readonly List<uint> instanceOffsets = new List<uint>();
        private readonly List<uint> pointerOffsets = new List<uint>();
        private readonly List<uint> resourceOffsets = new List<uint>();
        private readonly List<RiffArrayInfo> riffArrays = new List<RiffArrayInfo>();
        private uint riffArrayOffset;
        private uint riffStringOffset;
        private int exportedInstanceCount;
        private bool objectsRead;

        public override string RootType
        {
            get
            {
                if (!isValid || instanceOffsets.Count == 0)
                    return "";

                int typeIndex = GetInstanceTypeIndex(0);
                if (typeIndex < 0 || typeIndex >= typeGuids.Count)
                    return "RiffEbxAsset";

                Type type = TypeLibrary.GetType(typeGuids[typeIndex]);
                return type?.Name ?? "RiffEbxAsset";
            }
        }

        public static bool IsRiffEbx(Stream stream)
        {
            if (stream == null || !stream.CanSeek || stream.Length - stream.Position < 12)
                return false;

            long position = stream.Position;
            byte[] header = new byte[12];
            int read = stream.Read(header, 0, header.Length);
            stream.Position = position;

            return read == header.Length
                && BitConverter.ToUInt32(header, 0) == RiffMagic
                && BitConverter.ToUInt32(header, 8) == EbxMagic;
        }

        internal RiffEbxReader(Stream inStream)
            : base(inStream, true)
        {
            ReadContainer();
        }

        internal override void InternalReadObjects()
        {
            if (objectsRead)
                return;

            objectsRead = true;
            Dictionary<uint, object> objectsByOffset = new Dictionary<uint, object>();

            for (int i = 0; i < instanceOffsets.Count; i++)
            {
                int typeIndex = GetInstanceTypeIndex(i);
                Guid typeGuid = typeIndex >= 0 && typeIndex < typeGuids.Count ? typeGuids[typeIndex] : Guid.Empty;
                uint signature = typeIndex >= 0 && typeIndex < typeSignatures.Count ? typeSignatures[typeIndex] : 0;
                Type type = typeGuid != Guid.Empty ? TypeLibrary.GetType(typeGuid) : null;
                object obj = type != null ? TypeLibrary.CreateObject(type) : new RiffEbxAsset();

                Guid instanceGuid = Guid.Empty;
                if (i < exportedInstanceCount && instanceOffsets[i] >= 16)
                {
                    Position = payloadOffset + instanceOffsets[i] - 16;
                    instanceGuid = ReadGuid();
                }

                SetInstanceGuid(obj, new AssetClassGuid(instanceGuid, i));
                PopulateBasicProperties(obj, i, typeGuid, signature);

                objects.Add(obj);
                refCounts.Add(0);
                objectsByOffset[instanceOffsets[i]] = obj;
            }

            PopulateArrays(objectsByOffset);
        }

        private void ReadContainer()
        {
            Position = 0;
            if (ReadUInt() != RiffMagic)
                return;

            uint riffSize = ReadUInt();
            if (ReadUInt() != EbxMagic)
                return;

            long riffEnd = Math.Min(Length, 8L + riffSize);
            while (Position + 8 <= riffEnd)
            {
                uint chunk = ReadUInt();
                uint chunkSize = ReadUInt();
                long chunkStart = Position;
                long chunkEnd = chunkStart + chunkSize;
                if (chunkEnd > Length)
                    return;

                if (chunk == EbxdMagic)
                {
                    Pad(16);
                    payloadOffset = Position;
                }
                else if (chunk == EfixMagic)
                {
                    ReadFixup(chunkEnd);
                }
                else if (chunk == EbxxMagic)
                {
                    ReadExtra(chunkEnd);
                }

                Position = chunkStart + ((chunkSize + 1u) & ~1u);
            }

            if (payloadOffset == 0 || instanceOffsets.Count == 0 || typeGuids.Count == 0)
                return;

            arraysOffset = payloadOffset + riffArrayOffset;
            stringsOffset = payloadOffset + riffStringOffset;
            isValid = true;
        }

        private void ReadFixup(long chunkEnd)
        {
            fileGuid = ReadGuid();

            int typeCount = ReadSafeCount(chunkEnd, 16);
            for (int i = 0; i < typeCount; i++)
                typeGuids.Add(ReadGuid());

            int signatureCount = ReadSafeCount(chunkEnd, 4);
            for (int i = 0; i < signatureCount; i++)
                typeSignatures.Add(ReadUInt());

            exportedInstanceCount = ReadInt();

            int instanceCount = ReadSafeCount(chunkEnd, 4);
            for (int i = 0; i < instanceCount; i++)
                instanceOffsets.Add(ReadUInt());

            int pointerCount = ReadSafeCount(chunkEnd, 4);
            for (int i = 0; i < pointerCount; i++)
                pointerOffsets.Add(ReadUInt());

            int resourceCount = ReadSafeCount(chunkEnd, 4);
            for (int i = 0; i < resourceCount; i++)
                resourceOffsets.Add(ReadUInt());

            int importCount = ReadSafeCount(chunkEnd, 32);
            for (int i = 0; i < importCount; i++)
            {
                EbxImportReference import = new EbxImportReference
                {
                    FileGuid = ReadGuid(),
                    ClassGuid = ReadGuid()
                };
                imports.Add(import);
                if (!dependencies.Contains(import.FileGuid))
                    dependencies.Add(import.FileGuid);
            }

            SkipOffsetList(chunkEnd);
            SkipOffsetList(chunkEnd);

            if (Position + 12 <= chunkEnd)
            {
                riffArrayOffset = ReadUInt();
                boxedValuesOffset = payloadOffset + ReadUInt();
                riffStringOffset = ReadUInt();
            }
        }

        private void ReadExtra(long chunkEnd)
        {
            if (Position + 8 > chunkEnd)
                return;

            int arrayCount = ReadSafeCount(chunkEnd, 16);
            int boxedCount = ReadSafeCount(chunkEnd, 16);

            for (int i = 0; i < arrayCount && Position + 16 <= chunkEnd; i++)
            {
                riffArrays.Add(new RiffArrayInfo
                {
                    Offset = ReadUInt(),
                    Count = ReadInt(),
                    NameHash = ReadUInt(),
                    Flags = ReadUShort(),
                    TypeRef = ReadUShort()
                });
            }

            for (int i = 0; i < boxedCount && Position + 16 <= chunkEnd; i++)
                Position += 16;
        }

        private int ReadSafeCount(long chunkEnd, int itemSize)
        {
            if (Position + 4 > chunkEnd)
                throw new InvalidDataException("Truncated RIFF EBX fixup.");

            int count = ReadInt();
            if (count < 0 || count > 1000000 || Position + ((long)count * itemSize) > chunkEnd)
                throw new InvalidDataException("Invalid RIFF EBX fixup count.");
            return count;
        }

        private void SkipOffsetList(long chunkEnd)
        {
            int count = ReadSafeCount(chunkEnd, 4);
            Position += count * 4L;
        }

        private int GetInstanceTypeIndex(int instanceIndex)
        {
            if (instanceIndex < 0 || instanceIndex >= instanceOffsets.Count)
                return -1;

            long position = payloadOffset + instanceOffsets[instanceIndex];
            if (position < 0 || position + 2 > Length)
                return -1;

            Position = position;
            return ReadUShort();
        }

        private void PopulateBasicProperties(object obj, int instanceIndex, Guid typeGuid, uint signature)
        {
            if (obj is RiffEbxAsset raw)
            {
                raw.TypeGuid = typeGuid;
                raw.TypeSignature = signature;
                raw.Name = instanceIndex == 0 ? ReadAssetName() : "";
                raw.Dependencies.AddRange(dependencies);
            }
            else if (instanceIndex == 0)
            {
                SetName(obj, ReadAssetName());
            }

            uint start = instanceOffsets[instanceIndex];
            uint end = GetInstanceEnd(instanceIndex);
            List<ResourceRef> refs = new List<ResourceRef>();
            foreach (uint offset in resourceOffsets)
            {
                if (offset < start || offset + 8 > end)
                    continue;

                Position = payloadOffset + offset;
                refs.Add(new ResourceRef(ReadULong()));
            }

            if (obj is RiffEbxAsset rawAsset)
                rawAsset.ResourceReferences.AddRange(refs);

            AssignResourceProperties(obj, refs);
        }

        private uint GetInstanceEnd(int instanceIndex)
        {
            if (instanceIndex + 1 < instanceOffsets.Count)
            {
                uint next = instanceOffsets[instanceIndex + 1];
                if (instanceIndex + 1 < exportedInstanceCount && next >= 16)
                    next -= 16;
                return next;
            }

            if (riffArrayOffset > instanceOffsets[instanceIndex])
                return riffArrayOffset;
            if (riffStringOffset > instanceOffsets[instanceIndex])
                return riffStringOffset;
            return (uint)Math.Min(uint.MaxValue, Length - payloadOffset);
        }

        private string ReadAssetName()
        {
            long nameOffset = payloadOffset + riffStringOffset;
            if (riffStringOffset == 0 || nameOffset < 0 || nameOffset >= Length)
                return "";

            Position = nameOffset;
            return ReadNullTerminatedString();
        }

        private static void SetName(object obj, string name)
        {
            if (string.IsNullOrEmpty(name))
                return;

            PropertyInfo property = obj.GetType().GetProperty("Name");
            if (property == null || !property.CanWrite)
                return;

            if (property.PropertyType == typeof(CString))
                property.SetValue(obj, new CString(name));
            else if (property.PropertyType == typeof(string))
                property.SetValue(obj, name);
        }

        private static void SetInstanceGuid(object obj, AssetClassGuid guid)
        {
            MethodInfo method = obj.GetType().GetMethod("SetInstanceGuid", new[] { typeof(AssetClassGuid) });
            if (method != null)
            {
                method.Invoke(obj, new object[] { guid });
                return;
            }

            PropertyInfo property = obj.GetType().GetProperty("__InstanceGuid");
            if (property != null && property.CanWrite)
                property.SetValue(obj, guid);
        }

        private static void AssignResourceProperties(object obj, IList<ResourceRef> refs)
        {
            if (refs.Count == 0)
                return;

            List<PropertyInfo> properties = obj.GetType().GetProperties()
                .Where(property => property.CanWrite
                    && property.PropertyType == typeof(ResourceRef)
                    && property.GetCustomAttribute<IsTransientAttribute>() == null)
                .OrderBy(GetFieldOffset)
                .ToList();

            int count = Math.Min(properties.Count, refs.Count);
            for (int i = 0; i < count; i++)
                properties[i].SetValue(obj, refs[i]);
        }

        private void PopulateArrays(Dictionary<uint, object> objectsByOffset)
        {
            if (riffArrays.Count == 0)
                return;

            for (int instanceIndex = 0; instanceIndex < objects.Count; instanceIndex++)
            {
                object obj = objects[instanceIndex];
                List<PropertyInfo> properties = obj.GetType().GetProperties()
                    .Where(property => property.CanRead
                        && typeof(IList).IsAssignableFrom(property.PropertyType)
                        && property.PropertyType.IsGenericType
                        && property.GetCustomAttribute<IsTransientAttribute>() == null)
                    .OrderBy(GetFieldOffset)
                    .ToList();

                if (properties.Count == 0)
                    continue;

                uint instanceStart = instanceOffsets[instanceIndex];
                uint instanceEnd = GetInstanceEnd(instanceIndex);
                List<Tuple<uint, RiffArrayInfo>> candidates = FindArrayFields(instanceStart, instanceEnd);
                int count = Math.Min(properties.Count, candidates.Count);
                for (int i = 0; i < count; i++)
                    PopulateArrayProperty(obj, properties[i], candidates[i].Item2, objectsByOffset);
            }
        }

        private List<Tuple<uint, RiffArrayInfo>> FindArrayFields(uint start, uint end)
        {
            List<Tuple<uint, RiffArrayInfo>> result = new List<Tuple<uint, RiffArrayInfo>>();
            foreach (RiffArrayInfo array in riffArrays)
            {
                for (uint fieldOffset = start; fieldOffset + 4 <= end; fieldOffset += 4)
                {
                    Position = payloadOffset + fieldOffset;
                    int relativeOffset = ReadInt();
                    if ((long)fieldOffset + relativeOffset == array.Offset)
                    {
                        result.Add(Tuple.Create(fieldOffset - start, array));
                        break;
                    }
                }
            }
            return result.OrderBy(item => item.Item1).ToList();
        }

        private void PopulateArrayProperty(object obj, PropertyInfo property, RiffArrayInfo array, Dictionary<uint, object> objectsByOffset)
        {
            IList list = property.GetValue(obj) as IList;
            if (list == null && property.CanWrite)
            {
                list = Activator.CreateInstance(property.PropertyType) as IList;
                property.SetValue(obj, list);
            }
            if (list == null)
                return;

            list.Clear();
            Type elementType = property.PropertyType.GetGenericArguments()[0];
            Position = payloadOffset + array.Offset;

            for (int i = 0; i < array.Count; i++)
            {
                object value = ReadArrayValue(elementType, array.Offset + (uint)(Position - (payloadOffset + array.Offset)), objectsByOffset);
                if (value == null)
                    break;
                list.Add(value);
            }
        }

        private object ReadArrayValue(Type elementType, uint elementOffset, Dictionary<uint, object> objectsByOffset)
        {
            if (elementType == typeof(PointerRef))
            {
                int pointer = unchecked((int)ReadLong());
                if (pointer == 0)
                    return new PointerRef();
                if ((pointer & 1) == 1)
                {
                    int importIndex = pointer >> 1;
                    return importIndex >= 0 && importIndex < imports.Count
                        ? new PointerRef(imports[importIndex])
                        : new PointerRef();
                }

                uint targetOffset = unchecked((uint)(elementOffset + pointer));
                if (objectsByOffset.TryGetValue(targetOffset, out object target))
                    return new PointerRef(target);
                return new PointerRef();
            }
            if (elementType == typeof(ResourceRef))
                return new ResourceRef(ReadULong());
            if (elementType == typeof(int))
                return ReadInt();
            if (elementType == typeof(uint))
                return ReadUInt();
            if (elementType == typeof(short))
                return ReadShort();
            if (elementType == typeof(ushort))
                return ReadUShort();
            if (elementType == typeof(float))
                return ReadFloat();
            if (elementType == typeof(byte))
                return ReadByte();
            if (elementType == typeof(bool))
                return ReadByte() != 0;
            return null;
        }

        private static uint GetFieldOffset(PropertyInfo property)
        {
            EbxFieldMetaAttribute attribute = property.GetCustomAttribute<EbxFieldMetaAttribute>();
            return attribute?.Offset ?? uint.MaxValue;
        }
    }
}

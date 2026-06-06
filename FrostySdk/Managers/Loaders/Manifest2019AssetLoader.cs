using FrostySdk.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FrostySdk.Managers
{
    public partial class AssetManager
    {
        internal class Manifest2019AssetLoader : IAssetLoader
        {
            [Flags]
            private enum Flags
            {
                HasBaseBundles = 1 << 0,
                HasBaseChunks = 1 << 1,
                HasCompressedNames = 1 << 2
            }

            private struct CasFileIdentifier
            {
                public bool IsPatch;
                public uint InstallChunkIndex;
                public int CasIndex;

                public static CasFileIdentifier FromFileIdentifier(uint file)
                {
                    return new CasFileIdentifier
                    {
                        IsPatch = ((file >> 16) & 0xFF) != 0,
                        InstallChunkIndex = (file >> 8) & 0xFF,
                        CasIndex = (int)(file & 0xFF)
                    };
                }

                public static CasFileIdentifier FromFileIdentifier(uint file1, uint file2)
                {
                    return new CasFileIdentifier
                    {
                        IsPatch = ((file1 >> 16) & 0xFF) != 0,
                        InstallChunkIndex = ((file1 << 16) & 0xFFFF0000) | ((file2 >> 16) & 0xFFFF),
                        CasIndex = (int)(file2 & 0xFFFF)
                    };
                }
            }

            private readonly HashSet<Guid> removedChunks = new HashSet<Guid>();

            public void Load(AssetManager parent, BinarySbDataHelper helper)
            {
                foreach (CatalogInfo catalog in parent.fs.EnumerateCatalogInfos())
                {
                    foreach (string sbName in catalog.SuperBundles.Keys)
                    {
                        removedChunks.Clear();

                        SuperBundleEntry sbe = parent.superBundles.Find(a => a.Name == sbName);
                        int sbIndex;
                        if (sbe != null)
                        {
                            sbIndex = parent.superBundles.IndexOf(sbe);
                        }
                        else
                        {
                            parent.superBundles.Add(new SuperBundleEntry { Name = sbName });
                            sbIndex = parent.superBundles.Count - 1;
                        }

                        parent.WriteToLog("Loading Data ({0})", sbName);

                        string tocPath = parent.fs.ResolvePath(string.Format("{0}.toc", sbName));
                        if (tocPath == "")
                        {
                            parent.WriteToLog("Warning: Could not locate superbundle toc {0}", sbName);
                            continue;
                        }

                        LoadSuperBundle(parent, helper, tocPath, sbIndex);
                    }
                }
            }

            private void LoadSuperBundle(AssetManager parent, BinarySbDataHelper helper, string tocPath, int sbIndex)
            {
                byte[] tocBuffer;
                using (NativeReader tocReader = new NativeReader(new FileStream(tocPath, FileMode.Open, FileAccess.Read), parent.fs.CreateDeobfuscator()))
                    tocBuffer = tocReader.ReadToEnd();

                using (NativeReader reader = new NativeReader(new MemoryStream(tocBuffer)))
                {
                    reader.Position += 4; // bundle hash map offset
                    uint bundleDataOffset = reader.ReadUInt(Endian.Big);
                    int bundlesCount = reader.ReadInt(Endian.Big);

                    reader.Position += 4; // chunk hash map offset
                    uint chunkGuidOffset = reader.ReadUInt(Endian.Big);
                    int chunksCount = reader.ReadInt(Endian.Big);

                    reader.Position += 8;
                    uint namesOffset = reader.ReadUInt(Endian.Big);
                    uint chunkDataOffset = reader.ReadUInt(Endian.Big);
                    int dataCount = reader.ReadInt(Endian.Big);
                    Flags flags = (Flags)reader.ReadInt(Endian.Big);

                    uint namesCount = 0;
                    uint tableCount = 0;
                    uint tableOffset = uint.MaxValue;
                    HuffmanDecoder huffmanDecoder = null;
                    if ((flags & Flags.HasCompressedNames) != 0)
                    {
                        namesCount = reader.ReadUInt(Endian.Big);
                        tableCount = reader.ReadUInt(Endian.Big);
                        tableOffset = reader.ReadUInt(Endian.Big);

                        huffmanDecoder = new HuffmanDecoder();
                        reader.Position = namesOffset;
                        huffmanDecoder.ReadEncodedData(reader, namesCount, Endian.Big);

                        reader.Position = tableOffset;
                        huffmanDecoder.ReadHuffmanTable(reader, tableCount, Endian.Big);
                    }

                    if (bundlesCount != 0)
                    {
                        NativeReader sbReader = null;
                        try
                        {
                            reader.Position = bundleDataOffset;
                            for (int i = 0; i < bundlesCount; i++)
                            {
                                int nameOffset = reader.ReadInt(Endian.Big);
                                uint bundleSize = reader.ReadUInt(Endian.Big);
                                long bundleOffset = reader.ReadLong(Endian.Big);

                                long curPos = reader.Position;
                                string name;
                                if (huffmanDecoder != null)
                                {
                                    name = huffmanDecoder.ReadHuffmanEncodedString(nameOffset);
                                }
                                else
                                {
                                    reader.Position = namesOffset + nameOffset;
                                    name = reader.ReadNullTerminatedString();
                                }
                                reader.Position = curPos;

                                if (bundleSize == uint.MaxValue || bundleOffset == -1)
                                    continue;

                                parent.bundles.Add(new BundleEntry { Name = name, SuperBundleId = sbIndex });
                                int bundleId = parent.bundles.Count - 1;

                                byte loadFlag = (byte)(bundleSize >> 30);
                                bundleSize &= 0x3FFFFFFF;

                                NativeReader bundleSource;
                                switch (loadFlag)
                                {
                                    case 0:
                                        if (sbReader == null)
                                            sbReader = new NativeReader(new FileStream(tocPath.Replace(".toc", ".sb"), FileMode.Open, FileAccess.Read));
                                        bundleSource = sbReader;
                                        break;
                                    case 1:
                                        bundleSource = reader;
                                        break;
                                    default:
                                        throw new InvalidDataException("Unknown Manifest2019 bundle load flag: " + loadFlag);
                                }

                                LoadBundle(parent, helper, bundleSource, bundleOffset, bundleSize, bundleId);
                            }
                        }
                        finally
                        {
                            sbReader?.Dispose();
                        }
                    }

                    if (chunksCount != 0)
                    {
                        uint[] chunkData = new uint[dataCount];
                        reader.Position = chunkDataOffset;
                        for (int i = 0; i < dataCount; i++)
                            chunkData[i] = reader.ReadUInt(Endian.Big);

                        reader.Position = chunkGuidOffset;
                        for (int i = 0; i < chunksCount; i++)
                        {
                            byte[] b = reader.ReadBytes(16);
                            Array.Reverse(b);
                            Guid guid = new Guid(b);

                            int index = reader.ReadInt(Endian.Big);
                            if (index == -1)
                            {
                                removedChunks.Add(guid);
                                continue;
                            }
                            if (removedChunks.Contains(guid))
                                continue;

                            byte fileFlag = (byte)(index >> 24);
                            index &= 0x00FFFFFF;

                            CasFileIdentifier file;
                            if (fileFlag == 1)
                            {
                                file = CasFileIdentifier.FromFileIdentifier(chunkData[index++]);
                            }
                            else if (fileFlag == 0x80 || fileFlag == 0x84)
                            {
                                file = CasFileIdentifier.FromFileIdentifier(chunkData[index++], chunkData[index++]);
                            }
                            else
                            {
                                throw new InvalidDataException("Unknown Manifest2019 chunk file flag: " + fileFlag);
                            }

                            uint offset = chunkData[index++];
                            uint size = chunkData[index];
                            int catalogIndex = parent.fs.GetCatalogIndexFromInstallChunkIndex(file.InstallChunkIndex);
                            if (catalogIndex == -1)
                                continue;

                            ChunkAssetEntry chunk = new ChunkAssetEntry
                            {
                                Id = guid,
                                Size = size,
                                Location = AssetDataLocation.CasNonIndexed,
                                ExtraData = new AssetExtraData
                                {
                                    CasPath = parent.fs.GetFilePath(catalogIndex, file.CasIndex, file.IsPatch),
                                    DataOffset = offset
                                },
                                IsTocChunk = true
                            };

                            if (!parent.chunkList.ContainsKey(chunk.Id))
                                parent.chunkList.Add(chunk.Id, chunk);
                        }
                    }
                }
            }

            private void LoadBundle(AssetManager parent, BinarySbDataHelper helper, NativeReader stream, long offset, uint size, int bundleId)
            {
                long curPos = stream.Position;
                stream.Position = offset;

                int bundleOffset = stream.ReadInt(Endian.Big);
                int bundleSize = stream.ReadInt(Endian.Big);
                uint locationOffset = stream.ReadUInt(Endian.Big);
                int totalCount = stream.ReadInt(Endian.Big);
                uint dataOffset = stream.ReadUInt(Endian.Big);
                stream.Position += 12;

                bool inlineBundle = !(bundleOffset == 0 && bundleSize == 0);

                stream.Position = offset + locationOffset;
                byte[] fileFlags = stream.ReadBytes(totalCount);

                DbObject bundle;
                CasFileIdentifier file = new CasFileIdentifier();
                int currentIndex = 0;

                if (inlineBundle)
                {
                    stream.Position = offset + bundleOffset;
                    using (BinarySbReader bundleReader = new BinarySbReader(stream.CreateViewStream(offset + bundleOffset, bundleSize), 0, parent.fs.CreateDeobfuscator()))
                        bundle = bundleReader.ReadDbObject();

                    stream.Position = offset + dataOffset;
                }
                else
                {
                    stream.Position = offset + dataOffset;
                    file = ReadCasFileIdentifier(stream, fileFlags[currentIndex++], file);
                    uint casOffset = stream.ReadUInt(Endian.Big);
                    int casSize = stream.ReadInt(Endian.Big);
                    int catalogIndex = parent.fs.GetCatalogIndexFromInstallChunkIndex(file.InstallChunkIndex);
                    if (catalogIndex == -1)
                        return;

                    string path = parent.fs.ResolvePath(parent.fs.GetFilePath(catalogIndex, file.CasIndex, file.IsPatch));
                    using (NativeReader casReader = new NativeReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                    {
                        using (BinarySbReader bundleReader = new BinarySbReader(casReader.CreateViewStream(casOffset, casSize), 0, parent.fs.CreateDeobfuscator()))
                            bundle = bundleReader.ReadDbObject();
                    }
                }

                ApplyCasLocations(stream, bundle.GetValue<DbObject>("ebx"), fileFlags, ref currentIndex, ref file, parent);
                ApplyCasLocations(stream, bundle.GetValue<DbObject>("res"), fileFlags, ref currentIndex, ref file, parent);
                ApplyCasLocations(stream, bundle.GetValue<DbObject>("chunks"), fileFlags, ref currentIndex, ref file, parent);

                parent.ProcessBundleEbx(bundle, bundleId, helper);
                parent.ProcessBundleRes(bundle, bundleId, helper);
                parent.ProcessBundleChunks(bundle, bundleId, helper);

                stream.Position = curPos;
            }

            private void ApplyCasLocations(NativeReader stream, DbObject list, byte[] fileFlags, ref int currentIndex, ref CasFileIdentifier currentFile, AssetManager parent)
            {
                if (list == null)
                    return;

                for (int i = 0; i < list.Count; i++)
                {
                    currentFile = ReadCasFileIdentifier(stream, fileFlags[currentIndex++], currentFile);
                    DbObject obj = list[i] as DbObject;
                    int catalogIndex = parent.fs.GetCatalogIndexFromInstallChunkIndex(currentFile.InstallChunkIndex);
                    if (catalogIndex == -1)
                        throw new InvalidDataException("Unknown install chunk persistent index: " + currentFile.InstallChunkIndex.ToString("X8"));

                    obj.SetValue("catalog", catalogIndex);
                    obj.SetValue("cas", currentFile.CasIndex);
                    obj.SetValue("offset", stream.ReadInt(Endian.Big));
                    obj.SetValue("size", stream.ReadInt(Endian.Big));
                    if (currentFile.IsPatch)
                        obj.SetValue("patch", true);
                }
            }

            private CasFileIdentifier ReadCasFileIdentifier(NativeReader stream, byte flag, CasFileIdentifier current)
            {
                switch (flag)
                {
                    case 0:
                        return current;
                    case 1:
                        return CasFileIdentifier.FromFileIdentifier(stream.ReadUInt(Endian.Big));
                    case 0x80:
                    case 0x84:
                        return CasFileIdentifier.FromFileIdentifier(stream.ReadUInt(Endian.Big), stream.ReadUInt(Endian.Big));
                    default:
                        throw new InvalidDataException("Unknown Manifest2019 file identifier flag: " + flag);
                }
            }

            private class HuffmanNode : IComparable<HuffmanNode>
            {
                public bool IsLeaf => Left == null && Right == null;
                public char Letter => (char)(~Value);

                public uint Value;
                public HuffmanNode Left;
                public HuffmanNode Right;
                public HuffmanNode Parent;

                public HuffmanNode()
                {
                }

                public HuffmanNode(uint value, HuffmanNode left, HuffmanNode right)
                {
                    Value = value;
                    SetLeftNode(left);
                    SetRightNode(right);
                }

                public HuffmanNode(NativeReader stream, Endian endian)
                {
                    Value = stream.ReadUInt(endian);
                }

                public void SetLeftNode(HuffmanNode leftNode)
                {
                    Left = leftNode;
                    Left.Parent = this;
                }

                public void SetRightNode(HuffmanNode rightNode)
                {
                    Right = rightNode;
                    Right.Parent = this;
                }

                public int CompareTo(HuffmanNode other)
                {
                    return other == null ? 1 : Value.CompareTo(other.Value);
                }
            }

            private class HuffmanDecoder
            {
                private HuffmanNode rootNode;
                private int[] data;

                public void ReadHuffmanTable(NativeReader stream, uint count, Endian endian)
                {
                    rootNode = null;
                    HuffmanNode leftNode = null;
                    HuffmanNode rightNode = null;
                    List<HuffmanNode> nodes = new List<HuffmanNode>();
                    uint nodeValue = 0;

                    for (int i = 0; i < count; i++)
                    {
                        HuffmanNode node = new HuffmanNode(stream, endian);
                        int idx = nodes.FindIndex(a => a.Value == node.Value);
                        if (idx != -1)
                            node = nodes[idx];

                        if (leftNode == null)
                        {
                            leftNode = node;
                        }
                        else if (rightNode == null)
                        {
                            rightNode = node;
                            if (idx == -1)
                                nodes.Add(rightNode);

                            node = new HuffmanNode(nodeValue++, leftNode, rightNode);
                            rootNode = node;

                            leftNode = null;
                            rightNode = null;
                            idx = -1;
                        }

                        if (idx == -1)
                            nodes.Add(node);
                    }
                }

                public void ReadEncodedData(NativeReader stream, uint integerCount, Endian endian)
                {
                    data = new int[(int)integerCount];
                    for (int i = 0; i < integerCount; i++)
                        data[i] = stream.ReadInt(endian);
                }

                public string ReadHuffmanEncodedString(int bitIndex)
                {
                    if (rootNode == null || data == null)
                        throw new InvalidDataException("HuffmanDecoder state is not initialized.");

                    int dataLengthInBits = data.Length * 32;
                    StringBuilder sb = new StringBuilder();
                    while (true)
                    {
                        HuffmanNode node = rootNode;
                        while (!node.IsLeaf && bitIndex < dataLengthInBits)
                        {
                            int bit = (data[bitIndex / 32] >> (bitIndex % 32)) & 1;
                            node = bit == 0 ? node.Left : node.Right;
                            bitIndex++;
                        }

                        if (node.Letter == 0x00 || bitIndex >= dataLengthInBits)
                            return sb.ToString();

                        sb.Append(node.Letter);
                    }
                }
            }
        }
    }
}

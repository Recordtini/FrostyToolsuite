using FrostySdk;
using FrostySdk.BaseProfile;
using FrostySdk.Deobfuscators;
using FrostySdk.Interfaces;
using System;
using System.Collections.Generic;

namespace FrostyCmd
{
    internal class CollegeFootball27Profile : IProfile
    {
        private readonly BaseFrostyProfile baseProfile = new BaseFrostyProfile();

        public Type BinarySbReaderType => baseProfile.BinarySbReaderType;
        public Type CompressionUtilsType => typeof(CollegeFootball27CompressionUtils);

        public IBinarySbReader GetBinarySbReader() => baseProfile.GetBinarySbReader();
        public ICompressionUtils GetCompressionUtils() => new CollegeFootball27CompressionUtils();

        public virtual Profile CreateProfile()
        {
            return Create("CollegeFB27", "College Football 27 Playtest", this);
        }

        protected static Profile Create(string name, string displayName, IProfile profileData)
        {
            return new Profile
            {
                Name = name,
                DisplayName = displayName,
                ProfileData = profileData,
                DataVersion = (int)ProfileVersion.Madden20,
                CacheName = "collegefb27",
                Deobfuscator = typeof(NullDeobfuscator).Name,
                AssetLoader = "Manifest2019AssetLoader",
                Sources = new List<FileSystemSource>
                {
                    new FileSystemSource { Path = "Patch", SubDirs = false },
                    new FileSystemSource { Path = "Update", SubDirs = true },
                    new FileSystemSource { Path = "Data", SubDirs = false }
                },
                SDKFilename = "MADDEN20SDK",
                Banner = new byte[0],
                EbxVersion = 4,
                RequiresKey = false,
                MustAddChunks = false,
                EnableExecution = false,
                ContainsEAC = true,
                DefaultDiffuse = "Longshot/Common/Debug_Grey",
                DefaultNormals = "content/common/textures/debug/debug_texture_norm",
                DefaultMask = "content/Common/textures/debug/debug_texture_coeff",
                DefaultTint = "Longshot/Common/Debug_Grey",
                SharedBundles = new Dictionary<int, string>(),
                IgnoredResTypes = new List<uint>()
            };
        }
    }

    internal class CollegeFootball27TrialProfile : CollegeFootball27Profile
    {
        public override Profile CreateProfile()
        {
            return Create("CollegeFB27_Trial", "College Football 27 Playtest Trial", this);
        }
    }

    internal class CollegeFootball27CompressionUtils : ICompressionUtils
    {
        public string GetOodleDllName(string basePath) => basePath + "oo2core_9_win64.dll";
        public string GetZStdDllName() => "thirdparty/libzstd.1.3.4.dll";
        public bool LoadOodle => true;
        public bool LoadZStd => true;
        public int OodleCompressionLevel => 18;
    }
}

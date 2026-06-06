using FrostySdk.IO;

namespace FrostySdk.BaseProfile
{
    public sealed class Manifest2019BinarySbReader : BaseBinarySbReader
    {
        public Manifest2019BinarySbReader()
            : base(Endian.Little)
        {
        }
    }
}

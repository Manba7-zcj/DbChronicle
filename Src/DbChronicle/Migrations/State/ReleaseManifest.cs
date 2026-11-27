using System.Collections.Generic;

namespace DbChronicle.Migrations.State
{
    public sealed class ReleaseManifest
    {
        public string Name { get; set; } = "";
        public string SealedAt { get; set; } = "";
        public string SealedBy { get; set; } = "";
        public string DirHash { get; set; } = "";
        public string ChainHead { get; set; }
        public List<ReleaseUnit> Units { get; } = new List<ReleaseUnit>();
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System;

using DbChronicle.Configuration;
using DbChronicle.Models;

namespace DbChronicle.Migrations.State
{
    public sealed class ReleaseManifestStore
    {
        public const string Suffix = ".release.xml";

        private readonly DbChronicleConfig _config;

        public ReleaseManifestStore(DbChronicleConfig config)
        {
            _config = config;
        }

        public string Directory
        {
            get { return _config.ResolveReleasesPath(); }
        }

        public string PathFor(string release)
        {
            return Path.Combine(Directory, (release ?? "") + Suffix);
        }

        public bool Exists(string release)
        {
            return File.Exists(PathFor(release));
        }

        public ReleaseManifest Load(string release)
        {
            var path = PathFor(release);
            if (!File.Exists(path))
            {
                return null;
            }

            var document = XDocument.Load(path);
            var root = document.Root;
            if (root == null)
            {
                throw new InvalidOperationException("封版清单内容为空：" + path);
            }

            var manifest = new ReleaseManifest
            {
                Name = (string)root.Attribute("name") ?? release ?? "",
                SealedAt = (string)root.Attribute("sealedAt") ?? "",
                SealedBy = (string)root.Attribute("sealedBy") ?? "",
                DirHash = (string)root.Attribute("dirHash") ?? "",
                ChainHead = (string)root.Attribute("chainHead")
            };

            foreach (var element in root.Elements("unit"))
            {
                manifest.Units.Add(new ReleaseUnit
                {
                    Id = (string)element.Attribute("id") ?? "",
                    Checksum = (string)element.Attribute("checksum") ?? ""
                });
            }

            return manifest;
        }

        public IReadOnlyList<ReleaseManifest> LoadAll()
        {
            var result = new List<ReleaseManifest>();
            if (!System.IO.Directory.Exists(Directory))
            {
                return result;
            }

            foreach (var path in System.IO.Directory.GetFiles(Directory, "*" + Suffix).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(path);
                var root = document.Root;
                if (root == null)
                {
                    continue;
                }

                var manifest = new ReleaseManifest
                {
                    Name = (string)root.Attribute("name") ?? Path.GetFileName(path).Replace(Suffix, ""),
                    SealedAt = (string)root.Attribute("sealedAt") ?? "",
                    SealedBy = (string)root.Attribute("sealedBy") ?? "",
                    DirHash = (string)root.Attribute("dirHash") ?? "",
                    ChainHead = (string)root.Attribute("chainHead")
                };

                foreach (var element in root.Elements("unit"))
                {
                    manifest.Units.Add(new ReleaseUnit
                    {
                        Id = (string)element.Attribute("id") ?? "",
                        Checksum = (string)element.Attribute("checksum") ?? ""
                    });
                }

                result.Add(manifest);
            }

            return result;
        }

        public ReleaseManifest Freeze(string release, IEnumerable<ChangeUnit> units, string sealedBy)
        {
            var manifest = new ReleaseManifest
            {
                Name = release,
                SealedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                SealedBy = string.IsNullOrEmpty(sealedBy) ? Environment.UserName : sealedBy
            };

            foreach (var unit in units.Where(x => string.Equals(x.Release, release, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
            {
                manifest.Units.Add(new ReleaseUnit { Id = unit.Id, Checksum = unit.Checksum ?? "" });
            }

            manifest.DirHash = ComputeDirHash(manifest.Units);
            return manifest;
        }

        public string Save(ReleaseManifest manifest)
        {
            var path = PathFor(manifest.Name);
            if (File.Exists(path))
            {
                throw new InvalidOperationException("封版清单已存在（不可修改）：" + path);
            }

            System.IO.Directory.CreateDirectory(Directory);
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("release",
                    new XAttribute("name", manifest.Name ?? ""),
                    new XAttribute("sealedAt", manifest.SealedAt ?? ""),
                    new XAttribute("sealedBy", manifest.SealedBy ?? ""),
                    new XAttribute("dirHash", manifest.DirHash ?? ""),
                    new XAttribute("chainHead", manifest.ChainHead ?? "")));

            foreach (var unit in manifest.Units)
            {
                document.Root.Add(new XElement("unit",
                    new XAttribute("id", unit.Id ?? ""),
                    new XAttribute("checksum", unit.Checksum ?? "")));
            }

            document.Save(path);
            return path;
        }

        public static string ComputeDirHash(IEnumerable<ReleaseUnit> units)
        {
            var builder = new StringBuilder();
            foreach (var unit in units.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(unit.Id).Append(':').Append(unit.Checksum).Append('\n');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    text.Append(b.ToString("x2"));
                }

                return text.ToString();
            }
        }
    }
}

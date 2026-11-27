using System;

using DbChronicle.Migrations.State;
using DbChronicle.Parsing;

namespace DbChronicle.Cli
{
    public static partial class Commands
    {
        public static int Release(CliArguments args)
        {
            var config = LoadConfig(args);
            var release = args.Get("freeze", null);
            if (string.IsNullOrEmpty(release))
            {
                throw new CliFailureException(ExitCodes.Usage, "用法：release --freeze <yyyyMMdd> [--sealed-by <who>]");
            }

            release = release.Trim();
            if (!IsReleaseDate(release))
            {
                throw new CliFailureException(ExitCodes.Usage, "release 必须为 yyyyMMdd：" + release);
            }

            var store = new ReleaseManifestStore(config);
            if (store.Exists(release))
            {
                throw new CliFailureException(ExitCodes.Usage, "封版清单已存在（不可修改）：" + store.PathFor(release));
            }

            var units = new ChangeUnitLoader(config).LoadAll();
            var manifest = store.Freeze(release, units, args.Get("sealed-by", null));
            if (manifest.Units.Count == 0)
            {
                throw new CliFailureException(ExitCodes.Usage, "该 release 下没有变更单元：" + release);
            }

            var path = store.Save(manifest);
            Console.WriteLine("已封版：release " + release + "，单元 " + manifest.Units.Count + " 个");
            Console.WriteLine("清单：" + path);
            Console.WriteLine("dirHash：" + manifest.DirHash);
            return ExitCodes.Success;
        }

        private static bool IsReleaseDate(string value)
        {
            if (value.Length != 8)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (ch < '0' || ch > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}

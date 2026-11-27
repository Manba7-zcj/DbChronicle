using System;
using System.Text;

namespace DbChronicle.EmbeddedDemo
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
            }

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
            }

            var command = args[0].ToLowerInvariant();
            var arguments = CliArguments.Parse(Slice(args, 1));

            try
            {
                switch (command)
                {
                    case "compile":
                        return CliCommands.Compile(arguments);
                    case "check":
                        return CliCommands.Check(arguments);
                    case "apply":
                    case "upgrade":
                        return CliCommands.Apply(arguments);
                    case "status":
                        return CliCommands.Status(arguments);
                    case "verify":
                    case "verify-checksum":
                        return CliCommands.Verify(arguments);
                    case "embedded":
                        return EmbeddedApiSample.Run(arguments);
                    default:
                        Console.Error.WriteLine("未知命令：" + command);
                        PrintUsage();
                        return ExitCodes.Usage;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return ExitCodes.Execution;
            }
        }

        private static string[] Slice(string[] args, int start)
        {
            var result = new string[Math.Max(0, args.Length - start)];
            Array.Copy(args, start, result, 0, result.Length);
            return result;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DbChronicle 宿主内嵌 Demo（直接引用 DbChronicle.dll，不引用 CLI 程序集）");
            Console.WriteLine();
            Console.WriteLine("用法：DbChronicle.EmbeddedDemo <command> [options]");
            Console.WriteLine();
            Console.WriteLine("  compile   与 CLI compile 等价（支持 --change-id / --since / --pending / --split / --all-dialects）");
            Console.WriteLine("  check     与 CLI check 等价（--strict-warnings）");
            Console.WriteLine("  apply     与 CLI apply 等价（--dry-run / --restart / --mark-applied / --continue-on-error");
            Console.WriteLine("            / --check-drift / --backfill-logs），别名 upgrade");
            Console.WriteLine("  status    与 CLI status 等价（--release）");
            Console.WriteLine("  verify    与 CLI verify-checksum 等价");
            Console.WriteLine("  embedded  走 IDbChronicle 门面：LoadUnits / Check / DbUpgrade（--dry-run）");
            Console.WriteLine();
            Console.WriteLine("全局参数（与 CLI 同名同义）：--config / --no-config / --migrations / --dialect / --db-version");
            Console.WriteLine("  --environment / --version-log / --output-dir / --baseline / --baseline-frozen / --baseline-frozen-at");
            Console.WriteLine("  --quote-identifiers / --sql-comments / --include-rollback");
            Console.WriteLine("  --incremental-modify / --statement-delimiter / --lock-timeout / --commit-scope / --commit-batch-size");
            Console.WriteLine();
            Console.WriteLine("连接串：环境变量 DBCHRONICLE_CONN（例：Data Source=demo.db）");
            Console.WriteLine("示例（读配置文件）：");
            Console.WriteLine("  set DBCHRONICLE_CONN=Data Source=demo.db");
            Console.WriteLine("  DbChronicle.EmbeddedDemo compile");
            Console.WriteLine("  DbChronicle.EmbeddedDemo apply --dry-run");
            Console.WriteLine("  DbChronicle.EmbeddedDemo apply");
            Console.WriteLine("  DbChronicle.EmbeddedDemo status");
            Console.WriteLine("  DbChronicle.EmbeddedDemo embedded");
            Console.WriteLine("示例（纯参数，不读也不生成配置文件）：");
            Console.WriteLine("  DbChronicle.EmbeddedDemo compile --no-config --migrations migrations --dialect sqlite --db-version 3.35");
            Console.WriteLine("  DbChronicle.EmbeddedDemo apply --no-config --migrations migrations --dialect sqlite --db-version 3.35");
        }
    }
}

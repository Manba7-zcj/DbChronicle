using System;

using DbChronicle.Embedded;
using DbChronicle.Executor.AdoNet;
using DbChronicle.Migrations.Templates;
using DbChronicle.Models;

namespace DbChronicle.EmbeddedDemo
{
    internal static class EmbeddedApiSample
    {
        public static int Run(CliArguments args)
        {
            var config = Configurator.Load(args);
            var factory = new AdoNetExecutorFactory(config.Database.Dialect);
            var connectionString = ConnectionStringResolver.Resolve();

            using (var executor = factory.Create(connectionString))
            using (var chronicle = DbChronicleFactory.Create(executor, options =>
            {
                options.Config = config;
                options.Log = Console.WriteLine;
            }))
            {
                Console.WriteLine("配置方言：" + chronicle.Config.Database.Dialect + " " + chronicle.Config.Database.Version);
                Console.WriteLine("变更单元：" + chronicle.LoadUnits().Count + " 个");

                var diagnostics = chronicle.Check();
                Console.WriteLine("检查诊断：" + diagnostics.Count + " 条");
                foreach (var diagnostic in diagnostics)
                {
                    Console.WriteLine("  " + diagnostic);
                }

                var result = chronicle.DbUpgrade(new DbUpgradeOptions
                {
                    DryRun = args.Has("dry-run"),
                    AppliedBy = ChangeUnitFactory.ResolveAuthor(),
                    ConnectionString = connectionString,
                    LockFactory = factory,
                    ContinueOnError = args.Has("continue-on-error") ? true : (bool?)null
                });

                Console.WriteLine("DbUpgrade：success=" + result.Success
                    + "，应用 " + result.AppliedCount
                    + "，跳过 " + result.SkippedCount
                    + "，失败 " + result.FailedCount
                    + (string.IsNullOrEmpty(result.Message) ? "" : "，" + result.Message));

                return result.Success ? ExitCodes.Success : (result.ExitCode ?? ExitCodes.Execution);
            }
        }
    }
}

using System;

namespace DbChronicle.EmbeddedDemo
{
    internal static class ConnectionStringResolver
    {
        public const string EnvironmentVariable = "DBCHRONICLE_CONN";

        public static string Resolve()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            throw new InvalidOperationException(
                "未找到连接信息。请设置环境变量 " + EnvironmentVariable + "（例：Data Source=demo.db）");
        }
    }
}

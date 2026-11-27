using System;

namespace DbChronicle.Configuration
{
    public sealed class DatabaseSettings
    {
        public string Dialect { get; set; } = "mysql";
        public string Version { get; set; } = "8.0";

        /// <summary>能力级别（可选）：用于跨数据库版本升级的产物漂移检查；为空表示不启用。</summary>
        public string CapabilityLevel { get; set; }
    }
}
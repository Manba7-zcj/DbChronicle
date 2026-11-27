namespace DbChronicle.Configuration
{
    public sealed class OptionSettings
    {
        public bool QuoteIdentifiers { get; set; } = true;
        public bool IncludeComments { get; set; } = true;
        public bool IncludeRollback { get; set; }
        public string StatementDelimiter { get; set; } = ";";
        public int LockTimeoutSeconds { get; set; } = 10;
        public bool IncrementalModify { get; set; } = true;

        /// <summary>提交粒度：unit（整单元一个事务，默认）/ table（按表提交）/ batch（每 N 张表提交）。</summary>
        public string CommitScope { get; set; } = "unit";

        /// <summary>commitScope=batch 时每批的表数。</summary>
        public int CommitBatchSize { get; set; } = 10;

        /// <summary>引用存在性检查（L2）。关闭后，变更链中未声明的表（由既有系统创建）不再报 E111/E112。</summary>
        public bool StrictReferenceCheck { get; set; } = true;

        /// <summary>允许迟交（release 已过去后才补的变更单元）：E121 降为 W046。</summary>
        public bool AllowLateSubmission { get; set; }

        /// <summary>能力级别漂移处置：warn（默认，W043）或 strict（E120）。</summary>
        public string VersionDriftPolicy { get; set; } = "warn";
    }
}
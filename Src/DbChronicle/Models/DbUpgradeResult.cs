using System.Collections.Generic;

namespace DbChronicle.Models
{
    public sealed class DbUpgradeResult
    {
        public bool Success { get; set; }

        public int AppliedCount { get; set; }

        public int SkippedCount { get; set; }

        /// <summary>本次编译出的 SQL（含已跳过单元；dry-run 时即完整脚本）。</summary>
        public string Sql { get; set; } = "";

        public string Message { get; set; }

        /// <summary>成功时为空；任何失败都通过异常（DbChronicleException 及其子类）传递。</summary>
        public int? ExitCode { get; set; }

        /// <summary>continueOnError 模式下的失败明细；失败时由 MigrationFailedException 携带。</summary>
        public IReadOnlyList<string> Failures { get; set; } = new List<string>();

        /// <summary>continueOnError 模式下失败的单元数；失败时由 MigrationFailedException 携带。</summary>
        public int FailedCount { get; set; }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get; set; } = new List<CompileDiagnostic>();
}
}
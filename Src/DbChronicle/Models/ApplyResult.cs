using System.Collections.Generic;

namespace DbChronicle.Models
{
    public sealed class ApplyResult
    {
        public bool Success { get; set; }

        public int AppliedCount { get; set; }

        public int SkippedCount { get; set; }

        /// <summary>本次编译出的 SQL（含跳过单元的编译结果，dry-run 也可取）。</summary>
        public string Sql { get; set; } = "";

        public string Message { get; set; }

        /// <summary>成功时为空；任何失败都通过异常（DbChronicleException 及其子类）传递。</summary>
        public int? ExitCode { get; set; }

        /// <summary>continueOnError 模式下的失败明细；该模式结束时抛 MigrationFailedException 并携带同一份明细。</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>continueOnError 模式下失败的单元数（失败时通过 MigrationFailedException 传递）。</summary>
        public int FailedCount { get; set; }

        public List<CompileDiagnostic> Diagnostics { get; } = new List<CompileDiagnostic>();
    }
}
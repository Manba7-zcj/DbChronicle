using System.Collections.Generic;
using System;

using DbChronicle.Configuration;
using DbChronicle.Migrations.State;
using DbChronicle.Models;

namespace DbChronicle.Embedded
{
    /// <summary>
    /// 组件实例。宿主拿到实例后即可完成升级、编译、检查与状态查询。
    /// </summary>
    public interface IDbChronicle : IDisposable
    {
        /// <summary>生效的配置（BaseDirectory 为空时已按宿主程序目录补齐）。</summary>
        DbChronicleConfig Config { get; }

        /// <summary>
        /// 执行升级：读状态文件 → 编译 → 逐条执行 → 写状态文件 → 落审计日志。
        /// 使用工厂注入的 <see cref="Execution.IDbExecutor"/>，核心库不创建连接。
        /// 跨进程互斥需要同时提供 <see cref="DbUpgradeOptions.ConnectionString"/> 与
        /// <see cref="DbUpgradeOptions.LockFactory"/>；同一进程内的并发调用由组件串行化。
        /// </summary>
        DbUpgradeResult DbUpgrade(DbUpgradeOptions options = null);

        /// <summary>
        /// 只编译生成 SQL（离线，不连库）。changeId 为空表示全部单元。
        /// 变更目录为空或 changeId 未命中时抛 <see cref="InvalidOperationException"/>；编译错误同样抛异常，警告写入 Log。
        /// </summary>
        string Compile(string changeId = null);

        /// <summary>解析 + 方言编译 + 变更链语义校验（错误与警告一并返回），不连库。</summary>
        IReadOnlyList<CompileDiagnostic> Check();

        /// <summary>读取本地状态文件（含失败记录；判断是否已应用需过滤 <see cref="AppliedChange.HasFailure"/>）。</summary>
        IReadOnlyList<AppliedChange> Status();

        /// <summary>未应用的变更单元（状态文件中无记录，或记录为失败）。</summary>
        IReadOnlyList<ChangeUnit> PendingUnits();

        /// <summary>按 (release, change_id) 加载全部变更单元。</summary>
        IReadOnlyList<ChangeUnit> LoadUnits();
    }
}
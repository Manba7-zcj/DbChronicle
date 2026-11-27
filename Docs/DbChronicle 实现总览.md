# DbChronicle 实现总览

> 定位：本文件描述"当前代码实际怎么跑"，用于交接、评审与排障；设计口径以《DbChronicle — 数据库版本托管与升级工具集 方案文档》为准，检查规则细节见《DbChronicle 升级链合法性检查方案》。
>
> 版本：2026-11-27（对应工作区当前实现：升级链 L2/L3/L5 收尾 + 发布版本链 C2 + 能力版本链 C3 核心部分）。

---

## 1. 分层与工程

| 层 | 工程 | 说明 |
| --- | --- | --- |
| 核心库 | `Src/DbChronicle`（netstandard2.0，零第三方依赖） | 解析、编译、校验、执行编排、状态/审计、内嵌 API、扫描与代码生成 |
| CLI | `Src/DbChronicle.Cli`（net8.0 / net6.0 / net48） | 命令宿主：参数解析、插件加载、异常 → 退出码 |
| 执行器插件 | `DbChronicle.Executor.AdoNet`、`DbChronicle.Executor.EfCore` | `IDbExecutor` / `IDbLock` 实现（MySQL、MariaDB、PostgreSQL、SQL Server、SQLite） |
| 扫描插件 | `DbChronicle.Scanner.Source`、`DbChronicle.Scanner.Languages` | 源码 / 语言反向扫描 |
| 测试 | `Src/Tests`（Tests / Cli.Tests / IntegrationTests / Scanner.Tests） | 当前 264 + 44 + 35 + 7 = 350 条全绿 |

核心库约 16.4k 行，诊断码 65 个（`Src/DbChronicle/Diagnostics/DiagnosticCodes.cs`）。

约定：一个文件一个类型；枚举在 `Common/Enums`；目录名 = 命名空间；代码不写注释（需要说明的写进文档或 XML 的 `<description>`）。

---

## 2. 更新流程（端到端）

```
migrations/*.xml
  │ ① ChangeUnitLoader.LoadAll()
  │    读取 paths.migrations/*.xml（排除配置文件）
  │    → ChangeUnitParser（L1 形式校验）+ SHA-256 checksum
  │    → 按 (release, id) 排序：磁盘读取顺序不影响执行顺序
  ▼
check（离线）
  │ ② SchemaCompiler 逐单元编译（L6 方言可行性）
  │ ③ ChangeChecker（L2/L3/L4 + 数据节点 + 隐式状态剪枝）
  │ ④ ConsequenceChecker（L5 后果规则）
  │ ⑤ ReleaseChecker（E121 迟交 / E122 封版清单漂移）
  ▼
release --freeze <yyyyMMdd>   → releases/<release>.release.xml（不可变清单）
  ▼
compile                       先过 ChainValidator，再生成 SQL
  ▼
apply / upgrade               见 2.3
  ▼
status / verify-checksum / rollback / backfill-logs
```

### 2.1 单元加载与排序

- `ChangeUnitLoader.Sort`：`OrderBy(release).ThenBy(id)`，大小写不敏感；
- `release` 由解析器校验为 `yyyyMMdd`（否则 E006），所以字符串比较等价于时间比较；基线 `00000000` 自然排最前；
- `apply` 在 `SchemaExecutor.ApplyWith` 内会再排一次，避免调用方传入乱序集合；
- `compile` / `check` / 内嵌 API 使用的都是排好序的列表。

### 2.2 校验入口

| 入口 | 做什么 | 失败方式 |
| --- | --- | --- |
| `check` / `EmbeddedApi.Check()` | 编译 + 链校验（L2–L5、C2） | 返回诊断列表（不抛内容错误） |
| `compile` / `EmbeddedApi.Compile()` | 先 `ChainValidator`，再产出 SQL | CLI：`CliFailureException`（1）；API：链/编译错误 `ChainValidationException` / `CompilationException`（带 Diagnostics），找不到单元 `InvalidOperationException` |
| `apply` / `EmbeddedApi.DbUpgrade()` | 运行时全链路（2.3） | 抛 `DbChronicleException` 子类 |
| `status` | 读状态；运行期诊断（能力/空洞/顺序）只报告 | 打印诊断；有 Error 返回 3 |

### 2.3 执行阶段（`SchemaExecutor.ApplyWith`）

1. 排序 + 0 单元检查 → `MigrationConfigurationException`；
2. 静态链校验 `ChainValidator.Validate`（引用/发布版本）→ `ChainValidationException`；
3. 执行器方言 vs 配置方言 → `MigrationEnvironmentException`；
4. 库级锁（需 `ConnectionString` + `LockFactory`）→ `MigrationLockException`；
5. 读状态文件（`version_log/{environment}/DbchronicleState.xml`）；
6. 运行期校验 `ChainValidator.ValidateRuntime`（E120 能力漂移 / E123 版本空洞 / E127 顺序倒挂）→ `ChainValidationException`；
7. 已应用文件丢失、checksum 不一致 → `MigrationStateException`；
8. 逐单元：
   - 已应用且 checksum 一致 → 跳过；
   - 通配符 `table="*"` → 连接库读表清单后展开（失败 → `MigrationEnvironmentException`）；
   - 构建 `S(i-1)` 模型 + 编译（错误 → `CompilationException`）；
   - `--dry-run` 输出 SQL 后继续；
   - 执行：事务方言整单元事务 / `commitScope=table|batch` 按表提交 / MySQL 语句级断点续跑；
   - `continueOnError` 继续执行并累计失败；
9. 结束：有失败 → `MigrationFailedException`（携带 `Failures` / `AppliedCount`）；成功 → 写审计日志并返回。

### 2.4 状态与审计

| 产物 | 位置 | 内容 |
| --- | --- | --- |
| 状态文件 | `paths.versionLog`（默认 `./version_log/${environment}`） | `<applied>`：changeId、release、checksum、dialect、dialectVersion、capabilityLevel、appliedAt、appliedBy、failureAt、failureStmt；子节点 `<statements>`、`<tables>` |
| 发布清单 | `paths.releases`（默认 `./releases`） | `<release name sealedAt sealedBy dirHash chainHead>` + 每单元 id/checksum，生成后不可修改 |
| 审计日志 | 状态目录 | `{release}_dbupdate.md` + `sql/{release}/{id}.{dialect}.sql` |

---

## 3. 校验体系

| 层 / 轴 | 已实现 |
| --- | --- |
| L1 形式 | E001–E053：节点/属性/枚举/编码/文件名/release 格式/语句拆分 |
| L2 引用存在性 | E111 表、E112 列与 after 列、E113 重复 ADD（含同单元 `CREATE` 列与基线列）、E114 主键前提、E115 索引/约束/外键/序列、E116 外键目标、E117 RENAME 源、E118 seed 列 |
| L3 时序 | E105 改名后用旧名、E109 跨单元顺序、E119 `stage="before"` 不得引用本单元新增表/列 |
| L4 一致性 | E101–E104、E110 重复 id |
| L5 后果 | E054 类型/长度收窄、E055 主键/自增变更、E056 数据改写缺 `where`、E057 精度损失、E058 字符集收窄；W034 约束收紧、W035 Text→有界 String、W036 重命名、W037 类型族改变、W038 字符集/排序规则、W039 破坏性 rawSql、W040 Decimal 缺省精度 |
| C1 依赖图 | E107 depends 缺失、E108 补丁无 depends、E109 |
| C2 发布版本 | E121 迟交、W046 补丁迟交、E122 封版清单漂移、E123 版本空洞、E127 顺序倒挂 |
| C3 能力版本 | E120 strict 漂移、W043 warn 漂移、W045 状态缺 `capabilityLevel` |
| 数据节点 | `update` / `clean` 赋值列存在性、`seed` 行值个数（E118）；缺 `where` 归 E056 |

补充规则：

- **隐式状态剪枝**：检查器推进 `S(i)` 后，把"本单元未显式创建"的表/列移除，避免非法 ADD/MODIFY 生成的虚假状态污染后续单元；
- **通配符**：`table="*"` 在链校验中跳过，由 `apply` 连库展开；
- **既有表模式**：`options.strictReferenceCheck=false`（或 CLI `--no-strict-reference-check`）时，链中未声明的表不再报 E111/E112；零配置模式默认关闭；
- **`--strict-warnings`**：把 Warning 升级为失败（仅 `check`）。

### 3.1 顺序与版本语义

| 场景 | 码 | 说明 |
| --- | --- | --- |
| id 日期 > release 日期，非补丁/无声明 | E121 | 迟交，直接拒绝 |
| 补丁文件（`_fix_` / `_patch_`）或 `lateSubmission="true"` | W046 | 允许但提示 |
| 已封版 release 出现清单外单元 / checksum 变化 / 清单内文件缺失 | E122 | 清单是发布当时的快照 |
| 已封版 release 部分已应用 | E123 | 版本空洞；0 个已应用视为新环境，不报 |
| 待应用单元 `(release, id)` 低于已应用最高版本 | E127 | 顺序倒挂，`apply` 抛异常拒绝 |
| `--accept-late-submission` / `options.allowLateSubmission=true` | W046 | E121/E127 降级，显式接受迟交 |

---

## 4. 异常契约

内核只抛，CLI 与宿主各自接。

| 异常 | 含义 | ExitCode |
| --- | --- | --- |
| `ChainValidationException` | 链/发布/顺序/能力校验失败（带 `Diagnostics`） | 1 |
| `CompilationException` | 编译错误（带 `Diagnostics`） | 1 |
| `MigrationConfigurationException` | 空目录、dry-run + mark-applied 组合 | 1 |
| `MigrationStateException` | checksum 不一致、已应用文件丢失 | 3 |
| `StatementExecutionException` | 单条 SQL 执行失败（表名/序号/SQL/内部异常） | 3 |
| `MigrationFailedException` | `ContinueOnError` 结束汇总（`Failures`/`AppliedCount`） | 3 |
| `MigrationLockException` | 库级锁被占用 | 4 |
| `MigrationEnvironmentException` | 方言不匹配、取表清单/状态读失败 | 5 |
| 其他原始异常 | `XmlException` / `IOException` / 驱动异常等 | CLI 归 5 |

- `ApplyResult` / `DbUpgradeResult` 只在成功时返回（`Success=true`、`ExitCode=null`）；
- CLI `Commands.Apply`：`StatementExecutionException` → 3（`Describe()`）；其他 `DbChronicleException` → 打印诊断后按 `ExitCode`；未知异常 → 5；`Program.Main` 最外层兜底；
- 宿主：

```csharp
try
{
    var result = db.DbUpgrade(new DbUpgradeOptions { AppliedBy = "my-app" });
}
catch (ChainValidationException ex)
{
    foreach (var d in ex.Diagnostics) logger.LogError("{Diagnostic}", d.ToString());
}
catch (DbChronicleException ex)
{
    logger.LogError("升级失败（ExitCode={Code}）：{Message}", ex.ExitCode, ex.Message);
}
```

---

## 5. 入口

### 5.1 CLI

```text
db-chronicle init | new | compile | check | upgrade(apply) | status | verify-checksum
             release --freeze <yyyyMMdd> | rollback | scan | scan-db | codegen | plugin
```

常用参数：`--config` / `--no-config` / `--migrations` / `--dialect` / `--db-version` / `--environment` / `--version-log` / `--output-dir` / `--statement-delimiter` / `--lock-timeout` / `--commit-scope` / `--commit-batch-size` / `--strict-reference-check` / `--accept-late-submission` / `--releases` / `--capability-level` / `--version-drift-policy`；`apply` 另有 `--dry-run` / `--restart` / `--continue-on-error` / `--mark-applied` / `--check-drift` / `--backfill-logs`。

### 5.2 内嵌 API

```csharp
using var db = DbChronicleFactory.Create(executor, o =>
{
    o.ConfigPath = "DbchronicleConfig.xml";
    o.Log = message => logger.LogInformation(message);
});

var units = db.LoadUnits();
var diagnostics = db.Check();
var pending = db.PendingUnits();
var result = db.DbUpgrade(new DbUpgradeOptions
{
    AppliedBy = "my-app",
    ConnectionString = connectionString,
    LockFactory = lockFactory,
    ContinueOnError = null
});
```

### 5.3 核心编排入口

`DbChronicleMigrator.Run(MigratorOptions)` / `LoadUnits` / `CompileUnit` / `CheckDrift` / `BackfillLogs` —— 供工具形态使用，异常语义与 `SchemaExecutor` 一致。

---

## 6. 配置项

| 节点 | 键 | 默认 |
| --- | --- | --- |
| `database` | `dialect` / `version` / `capabilityLevel` | `mysql` / `8.0` / 空 |
| `environment` | 环境名 | `dev` |
| `paths` | `migrations` / `versionLog` / `output` / `releases` | `./migrations` / `./version_log/${environment}` / `./output` / `./releases` |
| `baseline` | `file` / `frozen` / `frozenAt` | 空 / `false` |
| `options` | `quoteIdentifiers` / `includeComments` / `includeRollback` | `true` / `true` / `false` |
| | `statementDelimiter` / `lockTimeoutSeconds` / `incrementalModify` | `;` / `10` / `true` |
| | `commitScope` / `commitBatchSize` | `unit` / `10` |
| | `strictReferenceCheck` / `allowLateSubmission` / `versionDriftPolicy` | `true` / `false` / `warn` |

宿主通过 `DbChronicleOptions.ConfigPath` 的相对路径按 `AppContext.BaseDirectory` 解析；`Config.BaseDirectory` 为空时同样按宿主程序目录补齐。

---

## 7. 测试与验证

```bash
dotnet build Src/DbChronicle.sln     # 0 错误（仅 net6.0 EOL 提示）
dotnet test  Src/DbChronicle.sln     # 264 + 44 + 35 + 7 = 350 全绿
```

覆盖重点：

- `ChainCheckTests`（27）：E111–E119、E054–E058、W034–W040、通配符、严格/宽松模式、隐式状态剪枝、数据节点与 seed；
- `ReleaseChainTests`（16）：E121/W046/E122/E123/E127、封版清单不可覆盖、读取顺序无关、W043/W045/E120；
- `FactoryTests`：内嵌 API 的 `Check/Compile/DbUpgrade`、异常类型（Chain/Compilation/Environment/Lock/State/Configuration/Failed）、空目录、dry-run+mark、状态文件损坏；
- `IntegrationTests`：SQLite 真执行下的表级/批级/单元级提交、续跑、checksum、文件丢失、continue-on-error。

`Test/DbChronicle.Demo` 的 `check` 通过（6 个单元，仅 W009/W022 提示）。

---

## 8. 未完成与已知取舍

| 项 | 状态 |
| --- | --- |
| W041 时间小数秒 / W042 PG `BYTEA` 长度 | 未实现（需要更细的类型模型与方言感知） |
| V 类探针 SQL、`verifyDataBeforeTighten` | 未实现（v1.1） |
| C3 的 W044 降级台账、SQL 快照 diff | 未实现；W043 目前只比较能力级别字段 |
| C2 的 E124 链头锚定、W047 多链头 | 未实现（默认关闭的可选机制） |
| 基线种子（E125/E126、W048/W049） | 未实现 |
| 检查顺序推进性能 | 仍为每单元全链重放（N 大时有成本） |
| `capabilityLevel` 命名口径 | 未定（例如 `mysql57` / `mysql80` / `pg14`） |
| 工作区提交状态 | 本轮及此前改动均未提交，提交时需按文件/hunk 拆分 |

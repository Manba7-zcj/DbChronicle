# DbChronicle 组件介绍 —— 架构 / 方言 / 类型映射 / 升级链路

> 用途：开发例会讲解「数据库升级组件」
> 基线：当前仓库实现（2026-11-25，net8.0 全量测试 273 通过 / 0 失败）
> 权威文档：《DbChronicle — 数据库版本托管与升级工具集 方案文档》v1.1
> 本文只描述已实现能力；类型映射表按内嵌资源 `Common/Resources/dialects/*.types.xml` 逐行核对

---

## 技术背景：运行期方言适配已由 ORM 解决，结构变更还没有

### 起因

中间件要在不同客户现场落地，底层数据库并不统一（MySQL / PostgreSQL / SQL Server / SQLite 都有存量）。如果业务代码直接拼 SQL，换一次库就等于重写整个 DAO 层。

项目因此引入 ORM 框架（FreeSql / SqlSugar），**初衷就是方言适配**——把"这段代码跑在哪种库上"从业务代码里拿掉：

| ORM 屏蔽的差异 | 典型表现 |
| --- | --- |
| 标识符与关键字 | MySQL 反引号 / PG 双引号 / SQL Server 方括号；`limit` 等关键字冲突 |
| 分页与函数 | `LIMIT/OFFSET` 与 `TOP` / 窗口函数；字符串聚合、日期函数各家不同 |
| 类型映射 | C# `int` ↔ `INT` / `INTEGER`；`DateTime` ↔ `DATETIME` / `TIMESTAMP` / `DATETIME2` |
| 事务与连接 | 统一连接池、事务边界、参数占位符 |

结果是一套实体定义与查询 API，落到哪种库由 ORM 的方言提供者翻译。**运行期 CRUD 与查询从此与方言解耦。**

### 缺口：DDL 没有同等的抽象层

升级脚本仍按某一种方言手写，于是出现"运行期跨库、升级不跨库"的断层：

| 关注点 | 运行期（ORM 已解决） | 结构变更（缺口） |
| --- | --- | --- |
| 方言翻译 | ORM 方言提供者自动翻译 | 手写 MySQL 语法，换库要重写一遍 |
| 版本化 | 不需要（无版本概念） | 靠文件名与人肉记忆 |
| 不可变性 | — | 已执行脚本可被改动，线上与仓库不一致 |
| 离线校验 | ORM 校验模型与映射 | 无法在 CI 里预演 SQL、无法检查冲突 |
| 破坏性控制 | 不涉及 | 删表 / 删列 / 改类型全凭自觉 |
| 审计 | 只有运行期 SQL 日志 | 没有"何时、谁、在哪个库、执行了什么" |

对照下来：**运行期已经"一套代码、多库运行"，结构层却还是"一库一套脚本"**，跨库部署的最后一块硬编码就在这里。

### 为什么不能直接用 ORM 的迁移功能

| ORM 迁移通常的做法 | 生产升级的实际要求 |
| --- | --- |
| 以实体类为事实来源 | 需要一份与编程语言无关、可评审的结构变更声明 |
| 生成"实体 vs 当前库"的差异 DDL | 需要不可变的历史变更链（已应用即冻结，不可回改） |
| 产物绑定 ORM 版本与能力 | 需要方言边界显式化（不支持就报错，不静默降级） |
| 无 checksum、无审计、无回滚规划 | 需要 checksum 强校验、审计留档、保守回滚 |
| 破坏性变更照做 | 需要评审流程（早期曾用 `allowDestructive` 门禁，2026-11-26 起已移除） |
| 差异 DDL 难以人工审阅（要对照库现状才能看懂） | 产物必须是能直接贴进评审单的 SQL |

一句话：ORM 迁移解决的是"把库补齐到实体当前的样子"，生产升级还需要"可审计、可回滚、不重复执行的历史"。

### 定位：结构变更层的"方言编译器"

DbChronicle 与 ORM 是同一思路的两个层次：

```
业务代码
  │  ORM：实体 / 查询 / 事务
  ▼
运行期 DML 与查询 ──── ORM 方言提供者 ────► MySQL / MariaDB / PostgreSQL / SQL Server / SQLite

migrations/*.xml
  │  DbChronicle：逻辑模型 / 变更单元
  ▼
版本化 DDL ─────────── DbChronicle 方言编译器 ───► MySQL / MariaDB / PostgreSQL / SQL Server / SQLite
```

| 层次 | 事实来源 | 方言适配方式 | 产物 |
| --- | --- | --- | --- |
| 运行期 | 实体类 | ORM 方言提供者 | DML / 查询 |
| 结构层 | 逻辑模型 XML | 方言类 + 类型映射表 | DDL + 状态 + 审计 |

两层共用同一套方言认知：逻辑类型与 ORM 的类型映射一一对应（`Int32` ↔ `INT` / `INTEGER`，`DateTimeOffset` ↔ `TIMESTAMP` / `TIMESTAMPTZ`）。存量库也可以直接 `scan --from database` 反向生成基线，接上 ORM 已经建好的结构，不需要从零手写。

这样"一套模型、多库部署"才闭环：**运行期由 ORM 翻译，结构层由 DbChronicle 翻译**，方言不再写进业务代码，也不再写进升级脚本。

---

## 0. 组件全景

```
                    ┌──────────────────────────────────────────────┐
                    │            宿主 / CI / DBA                   │
                    │  C# 进程内调用        任何语言 exec CLI       │
                    └───────────┬──────────────────┬───────────────┘
                                │                  │
                    ┌───────────▼──────┐  ┌────────▼─────────────────┐
                    │  DbChronicle     │  │  DbChronicle.Cli         │
                    │  核心库（内核）   │◄─┤  命令宿主 + 插件加载      │
                    │  netstandard2.0  │  │  net48 / net6.0 / net8.0 │
                    │  零第三方依赖     │  └────────┬─────────────────┘
                    └───────────┬──────┘           │ 按需加载
                                │                  │
        ┌───────────────────────┼──────────────────┼───────────────────────┐
        │                       │                  │                       │
┌───────▼────────┐   ┌──────────▼────────┐  ┌──────▼──────────┐  ┌─────────▼─────────┐
│ Executor.AdoNet│   │ Executor.EfCore   │  │ Scanner.Source  │  │ Scanner.Languages │
│ 4 驱动 + 库级锁 │   │ 复用宿主 DbContext│  │ Roslyn（C#）     │  │ Java/Go/TS 源码   │
└───────┬────────┘   └──────────┬────────┘  └─────────────────┘  └───────────────────┘
        │                       │
        ▼                       ▼
  MySQL / MariaDB / PostgreSQL / SQL Server / SQLite
```

一句话：**内核负责"把逻辑模型编译成目标方言的 SQL 并编排版本"，CLI 负责"命令宿主 + 按需装插件"，执行器插件负责"真正连库执行"。**

---

## 一、组件架构

### 1.1 交付物

| 交付物 | 形态 | 目标框架 | 依赖 | 职责 |
| --- | --- | --- | --- | --- |
| `DbChronicle` | 核心库（NuGet） | netstandard2.0 | **零第三方依赖** | 解析、编译、检查、版本编排、回滚规划、反向扫描、代码生成、管道协议 |
| `DbChronicle.Cli` | 命令行工具 | net48 / net6.0 / net8.0 | 只依赖核心库 | 12 个内建命令 + 插件宿主 |
| `DbChronicle.Executor.AdoNet` | 插件库 | net8.0 | MySqlConnector / Npgsql / Microsoft.Data.SqlClient / Microsoft.Data.Sqlite | 4 类数据库执行器 + 库级锁 |
| `DbChronicle.Executor.EfCore` | 插件库 | net8.0 | EF Core 6 | 复用宿主 `DbContext` 的执行器（应用内嵌） |
| `DbChronicle.Scanner.Source` | 插件库 | net8.0 | Roslyn 4.13 | C# 源码 → 基线 XML（免编译） |
| `DbChronicle.Scanner.Languages` | 插件库 | net8.0 | 无 | Java / Go / TypeScript 源码 → 基线 XML |

核心库的"零依赖"是硬约束：配置解析、类型映射、SQL 生成全部用 .NET 内置能力（含内嵌 XML 资源），因此内核可以被任何宿主引用而不带来版本冲突。

### 1.2 分层

| 层 | 组成 | 是否语言相关 |
| --- | --- | --- |
| 内核层（语言无关） | 解析、编译、方言、检查、版本编排、状态、审计、执行契约、内嵌 API | 否 |
| 工具层（语言无关） | CLI 命令与插件加载 | 否 |
| 反向工程层 | 数据库扫描（语言无关）+ C#/Java/Go/TS 源码扫描（语言相关） | 部分是 |
| 代码生成层 | XML → C# / Java / Go / TypeScript / C++ 实体 | 是 |
| 执行层 | ADO.NET / EF Core / 宿主自实现 | 否 |

### 1.3 内核功能点（`Src/DbChronicle/`，120+ 个源文件）

| # | 模块（目录） | 关键类型 | 功能 |
| --- | --- | --- | --- |
| 1 | `Configuration/` | `DbChronicleConfig`、`ConfigLoader`、各 Settings | 读/写 `DbchronicleConfig.xml`；路径变量 `${environment}` 解析；可零配置构造 |
| 2 | `Models/` | `ChangeUnit`、`TableModel`、`ColumnDefinition`、`StructureModel`、`CompileResult`、`StatementGroup`、`SqlStatement` | 逻辑模型对象树（26 个模型 + `Operations/` 24 个操作类） |
| 3 | `Parsing/` | `ChangeUnitLoader`、`ChangeUnitParser`、`ChecksumUtil` | 目录加载、XML → 对象、`SHA-256(原始字节)`、`(release, change_id)` 排序 |
| 4 | `Compilation/Compiler/` | `SchemaCompiler`、`SqlStatementSplitter` | 排序、操作分派、语句组装填、脚本拼装；方言感知的语句拆分 |
| 5 | `Compilation/Dialects/` | `ISqlDialect`、`SqlDialectBase`、5 个方言类、`TypeMapper`、`DialectFactory` | 每种数据库的 DDL 生成规则与类型映射 |
| 6 | `Compilation/Checks/` | `ChangeChecker` | 跨单元语义检查（重复 ADD、DROP 不存在列、RENAME 引用旧名、补丁缺 depends 等） |
| 7 | `Migrations/Executor/` | `SchemaExecutor`、`DbChronicleMigrator`、`SchemaIntrospector`、`StatementExecutionException` | 升级编排：锁 → 校验 → 执行 → 状态 → 审计；漂移复核；通配符表探测 |
| 8 | `Migrations/State/` | `VersionStore`、`VersionLogWriter` | 状态文件读写（含语句级断点）；审计 md + SQL 快照 |
| 9 | `Migrations/Rollback/` | `RollbackService` | 按 release 生成回滚计划（保守策略，blocker 清单） |
| 10 | `Execution/` | `IDbExecutor`、`IDbExecutorFactory`、`IDbLock`、`IDbLockFactory` | 执行器/锁契约，内核不实现任何驱动 |
| 11 | `Embedded/` | `DbChronicleFactory`、`IDbChronicle`、`DefaultDbChronicle`、`DbChronicleOptions` | 宿主内嵌 API：`DbUpgrade / Compile / Check / Status / PendingUnits / LoadUnits` |
| 12 | `Piping/` | `PipeProtocol`、`PipeRecordWriter`、`PipeRecordReader` | 分隔符文本协议（零落盘输出，供跨语言宿主逐条执行） |
| 13 | `Scanner/` | `CSharpScanner`、`BaselineXmlWriter`、`Database/*MetadataReader`、`DatabaseScanner` | 程序集反射扫描；5 方言数据库反向工程 → 基线草案 |
| 14 | `CodeGen/` | `EntityCodeGenerator`、`CodeNaming`、5 个 `*EntityWriter` | 由 migrations 生成 C# / Java / Go / TypeScript / C++ 实体 |
| 15 | `Diagnostics/` | `DiagnosticCodes` | E0xx / W0xx 诊断码集中定义 |
| 16 | `Common/` | `Enums/*`、`ColumnMerger`、`Resources/dialects/*.types.xml` | 枚举、增量 MODIFY 的列合并、5 份类型映射表（内嵌资源） |
| 17 | `Schema/` | `DbChronicle.ChangeUnit.xsd` | XML Schema，供 IDE 补全与校验 |

内核里几处值得单独说明的实现：

- **`StructureModelBuilder`**：回放 `migrations/` 到某个单元，得到"该时刻的表结构"。`incrementalModify` 模式下，`MODIFY` 只写变化的属性，其余由累积模型补全（因为 MySQL `MODIFY COLUMN` 是整列替换）。补不到时按列退化并给 `W014`。
- **`SchemaIntrospector`**：读目标库真实结构，用于 `--check-drift` 与 `table="*"` 通配符展开。
- **`SqlStatementSplitter`**：单遍字符状态机，识别引号、注释、`$$`、`GO`、`DELIMITER`、`BEGIN…END` 块；拆分失败在编译期报 `E042`。
- **`VersionStore`**：状态文件原子替换 + 有限重试 + 文件级并发保护；`<statements>` 只在无事务方言下使用。

**逻辑模型覆盖的操作全集（13 类，对应 24 个操作类）**

```
数据库结构操作
├── 1.  表级操作        CREATE / DROP / RENAME / COMMENT / ENGINE / CHARSET / TABLESPACE / REBUILD
├── 2.  列级操作        类型、长度、精度、可空、默认值、注释、自增、唯一、无符号、onUpdate
├── 3.  主键操作        ADD / DROP / REPLACE
├── 4.  索引操作        普通 / 唯一 / 复合 / 函数 / 部分 / 全文 / RENAME
├── 5.  外键操作        ADD / DROP / MODIFY（级联规则）
├── 6.  约束操作        唯一、检查、默认值（ADD / DROP）
├── 7.  注释操作        表、列、索引、约束
├── 8.  视图操作        CREATE / DROP（结构化 <select>，或按方言 <sql>）
├── 9.  序列操作        CREATE / MODIFY / DROP（PG、SQL Server）
├── 10. 触发器操作      按方言写 <sql>
├── 11. 存储过程/函数   按方言写 <sql>
├── 12. 数据操作        种子、更新、删除、清洗、迁移、回填
└── 13. 高级对象        分区、字符集、存储引擎、表空间、权限、同义词
```

哪些方言不支持、哪些必须改走 `<rawSql>`，见 2.4 / 2.5。

### 1.4 内嵌 API（C# 宿主）

```csharp
var db = DbChronicleFactory.Create(executor, o =>
{
    o.ConfigPath = "DbchronicleConfig.xml";   // 或 o.Config 传内存配置
    o.Log = message => logger.LogInformation(message);
});

var result = db.DbUpgrade(new DbUpgradeOptions { DryRun = false, AppliedBy = "my-app" });
```

| 成员 | 作用 |
| --- | --- |
| `DbUpgrade(options)` | 执行升级；可带 `DryRun` / `AppliedBy` / `ConnectionString` + `LockFactory` / `LockTimeoutSeconds` / `ContinueOnError` / `Restart` / `MarkAppliedChangeId`（与 `DryRun` 互斥）。所有失败都抛 `DbChronicleException` 子类（配置 1 / 状态 3 / 锁 4 / 环境 5 / 链校验 `ChainValidationException` / 编译 `CompilationException` / 执行 `StatementExecutionException` / 继续模式结束 `MigrationFailedException`），带 `ExitCode` 与诊断；成功才返回 `DbUpgradeResult` |
| `Compile(changeId?)` | 只编译，不执行；目录为空或 `changeId` 未命中时抛 `InvalidOperationException`，警告写入 `Log` |
| `Check()` | 解析 + 方言编译 + 变更链语义校验（等价 CLI `check`），返回诊断列表 |
| `Status()` | 读本地状态文件，返回已应用记录（含失败记录） |
| `PendingUnits()` | 返回未应用单元（状态文件无记录或记录为失败） |
| `LoadUnits()` | 加载并排序全部变更单元 |

组件**不内置 hosted service**：升级时机由宿主决定（启动时同步执行、或放进发布流水线）。

### 1.5 CLI 支持

`Program.cs` 分发表（内建命令优先于插件命令）：

| 命令 | 连库 | 作用 |
| --- | --- | --- |
| `init` | 否 | 生成 `DbchronicleConfig.xml` 与目录（`--dialect` / `--db-version`） |
| `new` | 否 | 生成变更单元（文件名/ID/作者/release 自动生成，`FileMode.CreateNew` 绝不覆盖） |
| `compile` | 否 | 编译为 SQL：默认写 `output/`；`--split` 每单元一文件；`--all-dialects` 五方言；`--pending` 只编未应用；`--emit pipe` / `--pipe <name>` 不落盘 |
| `check` | 否 | 语义检查；`--strict-warnings` 把 W 升级为失败（CI 门禁） |
| `upgrade`（别名 `apply`） | 是 | 执行升级：`--dry-run` / `--restart` / `--continue-on-error` / `--check-drift` / `--backfill-logs` / `--mark-applied <id>` |
| `status` | 否 | 读状态文件：当前版本、已应用、待应用（`--release` 过滤） |
| `release` | 否 | `--freeze <yyyyMMdd>` 生成不可变封版清单（`releases/<release>.release.xml`）；check/apply 据此检查 E121/E122/E123 |
| `verify` / `verify-checksum` | 否 | 校验已应用文件的 checksum（文件被改/丢失） |
| `scan` | 视 `--from` | `assembly`（反射，离线）/ `source`（插件扫描）/ `database`（连库只读） |
| `scan-db` | 是 | `scan --from database` 的别名 |
| `codegen` | 否 | 由 migrations 生成实体（`--lang csharp/java/go/typescript/cpp`） |
| `rollback` | 视 `--apply` | 按 `--target-release` 生成回滚计划；默认 dry-run |
| `plugin` | 否 | `list` / `enable` / `disable`（插件状态存用户目录，升级工具不恢复） |

全局选项：`--config <file>`、`--no-config`、`--plugin-dir <dir>`、`--unload-plugins`。

退出码（脚本与 CI 依赖）：

| 码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 参数 / 校验失败 |
| 2 | 有待应用变更（仅 `status`） |
| 3 | 执行或 checksum 校验失败 |
| 4 | 未取得库级锁 |
| 5 | 环境问题（连接失败、非交互环境缺连接串） |
| 6 | 缺少插件 |

### 1.6 插件支持

**插件契约**（都在 `DbChronicle.Cli/Plugins/`，插件只需引用 CLI 程序集）：

```csharp
[DbChroniclePlugin(Name = "scanner-source")]
public static class SourceScannerPlugin
{
    public static void Initialize(IPluginRegistry registry)
    {
        registry.AddSourceScanner("csharp", () => new SourceScanner());
    }
}
```

`IPluginRegistry` 四个注册点：

| 方法 | 注册的能力 |
| --- | --- |
| `AddExecutor(dialect, factory, locks?)` | 某方言的执行器工厂，可选同时提供库级锁工厂 |
| `AddSourceScanner(language, scanner)` | 某语言的源码扫描器（`scan --from source --lang`） |
| `AddCodeGenerator(language, writer)` | 某语言的实体生成器（内建优先，插件可覆盖） |
| `AddCommand(name, handler, help)` | 全新子命令（内建命令同名时内建优先） |

**加载机制**：`PluginHost` → `PluginLoadContext`（可卸载 AssemblyLoadContext）→ `PluginCatalog`（按能力取用）→ `PluginScope`（命令结束后卸载）。每个插件独占一个子目录 `plugins/<插件名>/`，删/禁用互不影响。能力缺失时抛 `PluginNotInstalledException`，CLI 输出"请把 … 部署到 plugins 目录"并返回退出码 6，而不是抛程序集加载异常。

**内置插件**：

| 插件目录 | 程序集 | 提供 |
| --- | --- | --- |
| `plugins/adonet/` | `DbChronicle.Executor.AdoNet.dll` | 4 类执行器（MySQL 复用给 MariaDB）+ 库级锁（`upgrade`、`scan --from database`、`rollback --apply`） |
| `plugins/scanner-source/` | `DbChronicle.Scanner.Source.dll` + Roslyn dll | C# 源码扫描 |
| `plugins/scanner-languages/` | `DbChronicle.Scanner.Languages.dll` | Java / Go / TS 源码扫描 |

EF Core 执行器不参与 CLI 插件目录：它是给宿主应用内嵌用的库。

**分发形态**：

| 方式 | 命令 | 体积/说明 |
| --- | --- | --- |
| dotnet tool（核心） | `dotnet tool install -g DbChronicle.Cli` | 约 0.18 MB；离线命令全部可用（init/new/compile/check/status/verify-checksum/rollback(dry-run)/codegen/scan --from assembly） |
| dotnet tool + 执行器 | `dotnet tool install -g DbChronicle.Cli.AdoNet` | 约 17 MB；追加 `upgrade` / `scan --from database` / `rollback --apply` |
| dotnet tool 全量 | `dotnet tool install -g DbChronicle.Cli.All` | 约 22 MB；装完可 `plugin disable` 关掉不用的 |
| zip 离线包 | `Tools/publish.ps1 -Plugins adonet\|all [-SelfContained]` | 内网 / 目标机无 .NET 运行时 |

---

## 二、支持的数据库方言

### 2.1 支持深度

| 深度 | 数据库 | 版本 | 说明 |
| --- | --- | --- | --- |
| 完整支持 | MySQL | 5.7 / 8.0+ | 5.7 与 8.0 的 `RENAME COLUMN` 语法差异由版本号区分 |
| 完整支持 | PostgreSQL | 12+ | 独立 `COMMENT ON`、`ALTER COLUMN` 拆多条、`TIMESTAMPTZ` |
| 完整支持 | SQL Server | 2016+ | 默认值约束命名、`sp_rename`、`NVARCHAR` |
| 兼容复用 | MariaDB | 10.2.7+ | 继承 MySQL 方言；`JSON` 无原生类型 → `LONGTEXT` + `CHECK` |
| 受限支持 | SQLite | 3.35.0+ | 不支持 `ALTER COLUMN`，以 rebuild 为主；无注释；外键需 `PRAGMA foreign_keys = ON` |

### 2.2 方言实现结构

```
方言实现结构

ISqlDialect（方言契约：决定「这个库能生成什么」）
├── 表           CreateTable / DropTable / RenameTable / CommentTable / TableOption
├── 列           AddColumn / ModifyColumn / DropColumn / RenameColumn / ReorderColumn / CommentColumn
├── 主键         AddPrimaryKey / DropPrimaryKey
├── 索引         CreateIndex / DropIndex / RenameIndex
├── 外键         AddForeignKey / DropForeignKey
├── 约束         Add / Drop × Unique / Check / Default
├── 注释         CommentIndex / CommentConstraint（PG、SQL Server 原生；其余降级 W031）
├── 其他对象     View / Sequence / RebuildTable / RequiresRebuild（rebuild 仅 SQLite）
├── 数据         Update / Delete / Clean / Backfill / BuildSeed
└── 基础设施     RawSql / FormatDefaultValue / Quote / EscapeString

SqlDialectBase（公共实现 + 默认行为：方言只覆盖差异，不支持则显式报错或警告）
├── MySqlDialect          反引号、MODIFY 整列重述、UNSIGNED / ON UPDATE
│   └── MariaDbDialect    只覆盖 JSON（LONGTEXT + CHECK）与约束名截断
├── PostgreSqlDialect     COMMENT ON、ALTER COLUMN 拆条、标识符统一小写
├── SqlServerDialect      DF_ 约束名、sp_rename、NVARCHAR / NCHAR
└── SqliteDialect         rebuild、注释跳过、CREATE INDEX IF NOT EXISTS
```

`DialectFactory.Create(dialectName, version, context)` 是唯一入口（版本缺省 8.0 / 10.5 / 14 / 2019 / 3.35）；`TypeMapper` 从内嵌资源读对应 `*.types.xml`。

### 2.3 对象与能力矩阵

| 对象 / 能力 | MySQL | MariaDB | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- | --- | --- |
| 序列 | ✗ | ✗ | ✓ | ✓ | ✗ |
| 存储过程 / 函数 | ✓ | ✓ | ✓ | ✓ | ✗ |
| 触发器 | ✓ | ✓ | ✓ | ✓ | ✓（受限） |
| 表注释 | ✓ | ✓ | ✓ | ✓（扩展属性） | ✗ |
| 索引注释 | ✗ | ✗ | ✓ | ✓（扩展属性） | ✗ |
| 约束注释 | ✗ | ✗ | ✓ | ✓（扩展属性） | ✗ |
| 存储引擎 | ✓ | ✓ | ✗ | ✗ | ✗ |
| 表级字符集 | ✓ | ✓ | ✗（库级） | ✗（列级 collation） | ✗ |
| 表空间 | ✓ 8.0+ | ✓ | ✓ | ✗（文件组） | ✗ |
| 独立 CHECK 约束 | ✓ 8.0+ | ✓ 10.2.1+ | ✓ | ✓ | ⚠️ 走 rebuild |
| 部分索引 | ✗ | ✗ | ✓ | ✗ | ✓ |
| 全文索引 | ✓ | ✓ | ⚠️ 需 `to_tsvector` 变通 | ⚠️ 需全文目录 | ✗ 需 FTS5 |
| 列顺序调整 | ✓ `AFTER` | ✓ | ✗ | ✗ | ✗ |
| JSON 原生类型 | ✓ | ✗（LONGTEXT+CHECK） | ✓ JSONB | ✗（NVARCHAR(MAX)） | ✗（TEXT） |

### 2.4 硬报错（编译失败，必须改模型）

| 写法 | 不支持的方言 | 码 | 替代方案 |
| --- | --- | --- | --- |
| `<sequence action="CREATE/MODIFY">` | MySQL / MariaDB / SQLite | E046 | 自增列，或 `<rawSql>` |
| `<table action="ENGINE">` | PostgreSQL / SQL Server / SQLite | E047 | MySQL 专属 |
| `<table action="CHARSET">` | PostgreSQL / SQL Server / SQLite | E047 | PG 建库时定 |
| `<table action="TABLESPACE">` | SQL Server / SQLite | E047 | 走 `<rawSql>` |
| `<column action="REORDER">` | PostgreSQL / SQL Server / SQLite | E044 | 删除，或用 `<rawSql>` 重建表 |
| `<view>` 无 `<select>` 且无本方言 `<sql>` | 全方言 | E048 | 补结构化 `select` 或 `<sql dialect>` |
| `<sequence>` 缺 `name` | 全方言 | E049 | 补 `name` |
| 表选项一个值都没给 | 全方言 | E050 | 至少给一项 |
| `DROP TABLE` / `DROP COLUMN` / `<delete>` | 全方言 | —（E030 已于 2026-11-26 移除） | 直接执行 |
| 复合主键含自增列 | SQLite | 编译期异常 | 自增列单独作主键 |
| `foreignKey="x"`（非 `表.列`） | 全方言 | E031 | 写 `表名.列名` |
| `PARTITION` / `GRANT` / `SYNONYM` 写成表级 action | 全方言 | E012 | 改 `<rawSql>` |
| 语句拆分失败 | 全方言 | E042 | 检查引号/块/`DELIMITER` 闭合 |

### 2.5 降级警告（能生成 SQL，语义弱化）

| 写法 | 受影响方言 | 码 | 实际生成 | 影响 |
| --- | --- | --- | --- | --- |
| `unsigned="true"` | PG / SS / SQLite | W001 | 忽略（有符号） | 需自行补 `CHECK (c >= 0)` |
| `onUpdate="..."` | PG / SS / SQLite | W002 | 忽略 | 需触发器实现 |
| `type="DateTimeOffset"` | MySQL / MariaDB | W003 | `TIMESTAMP` | 丢原始偏移、2038 上限 |
| `Json` / `Xml` 无原生类型 | MySQL / MariaDB（Xml）、SS（Json）、SQLite | W004 | `LONGTEXT` / `NVARCHAR(MAX)` / `TEXT` | 无原生校验与查询 |
| `Blob` + `length` | 全方言 | W005 | 忽略长度 | — |
| 表注释 / 列注释 | SQLite | W006 / W007 | 跳过 | 注释丢失 |
| 列级 `charset` / `collate` | PG / SS / SQLite | W015 | 忽略 | 由库级排序规则决定 |
| 列内联 `foreignKey` | 全方言 | W016 | 不生成外键 | 必须用独立 `<foreignKey>` 节点 |
| `FULLTEXT` 索引 | PG / SS / SQLite | W017 | 普通索引 | 无全文检索 |
| 检查约束 | MySQL 5.7 | W023 | 语法接受但不生效 | 约束不强制执行 |
| 默认值约束 | MySQL 5.7 | W024 | 不支持 SET DEFAULT | 需随 `MODIFY` 改 |
| 部分索引 `where` | MySQL / MariaDB / SS | W027 | 忽略 `where` | 索引覆盖全表 |
| 索引 `RENAME` | MySQL 5.7 以下 / SQLite | W028 | 跳过 | 改 DROP + CREATE |
| `seed conflict="UPDATE"` | SQL Server；PG/SQLite 无主键 | W029 | 普通 INSERT | 重复执行主键冲突 |
| 触发 SQLite rebuild | SQLite | W030 | 不恢复历史索引/CHECK | 需在本单元重建 |
| 索引注释 / 约束注释 | MySQL / MariaDB / SQLite | W031 | 跳过 | 无原生机制 |
| 视图/触发器/过程缺本方言 `<sql>` | 全方言 | W032 | 跳过 | 对象不会创建 |
| 数据迁移缺本方言 `<sql>` | 全方言 | W033 | 跳过 | 数据不会迁移 |

### 2.6 方言差异实现要点

| 方言 | 要点 |
| --- | --- |
| MySQL | `MODIFY COLUMN` 必须整列重述 → 默认 `incrementalModify` 由累积模型补全；`UNSIGNED` / `ON UPDATE` 原生支持；建表固定 `CREATE TABLE IF NOT EXISTS … ENGINE=InnoDB DEFAULT CHARSET=utf8mb4` |
| MariaDB | 继承 MySQL；`Json` 追加 `CHECK (col IS NULL OR JSON_VALID(col))`；约束名超 128 截断 + 8 位哈希 |
| PostgreSQL | 注释独立 `COMMENT ON`；`MODIFY` 拆成多条 `ALTER COLUMN`；`TIMESTAMPTZ` 保留时刻；`ALTER TABLE … SET TABLESPACE`；标识符统一小写 |
| SQL Server | 默认值必须命名约束 `DF_{table}_{column}`（超长截断+哈希）；`sp_rename`；字符列统一 `NVARCHAR/NCHAR`；注释走 `sp_addextendedproperty` upsert |
| SQLite | `ALTER COLUMN` / 加删主键 / 加删外键整体走 rebuild：建新表 → `INSERT … SELECT` → 删旧 → 改名；`ADD COLUMN` 不能带主键/唯一；索引带 `CREATE INDEX IF NOT EXISTS`；注释跳过；`SqliteExecutor` 打开连接后执行 `PRAGMA foreign_keys = ON` |

---

## 三、数据类型映射

### 3.1 逻辑类型（20 种）

数值 `Int8 / Int16 / Int32 / Int64`、`Float32 / Float64`、`Decimal(p,s)`；
布尔 `Boolean`；
字符串 `FixedString(n) / String(n) / Text / Json / Xml`；
时间 `DateTime / DateTimeOffset / Date / Time`；
二进制 `Binary(n) / Blob`；
标识 `Uuid`。

命名原则：**数值用位数（`Int32` 而非 `int`），其余用语义名**。这样映射到 MySQL `INT`、Java `int`、Go `int32`、TypeScript `number` 都不会有歧义。

### 3.2 五方言映射总表

| 逻辑类型 | MySQL | MariaDB | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- | --- | --- |
| `Boolean` | `TINYINT(1)` | `TINYINT(1)` | `BOOLEAN` | `BIT` | `INTEGER` |
| `Int8` | `TINYINT` | `TINYINT` | `SMALLINT` | `TINYINT` | `INTEGER` |
| `Int16` | `SMALLINT` | `SMALLINT` | `SMALLINT` | `SMALLINT` | `INTEGER` |
| `Int32` | `INT` | `INT` | `INTEGER` | `INT` | `INTEGER` |
| `Int64` | `BIGINT` | `BIGINT` | `BIGINT` | `BIGINT` | `INTEGER` |
| `Float32` | `FLOAT` | `FLOAT` | `REAL` | `REAL` | `REAL` |
| `Float64` | `DOUBLE` | `DOUBLE` | `DOUBLE PRECISION` | `FLOAT` | `REAL` |
| `Decimal(p,s)` | `DECIMAL(p,s)` | `DECIMAL(p,s)` | `NUMERIC(p,s)` | `DECIMAL(p,s)` | `NUMERIC` |
| `FixedString(n)` | `CHAR(n)` | `CHAR(n)` | `CHAR(n)` | `NCHAR(n)` | `TEXT` |
| `String(n)` | `VARCHAR(n)` | `VARCHAR(n)` | `VARCHAR(n)` | `NVARCHAR(n)` | `TEXT` |
| `Text` | `LONGTEXT` | `LONGTEXT` | `TEXT` | `NVARCHAR(MAX)` | `TEXT` |
| `Json` | `JSON` | `LONGTEXT` + CHECK | `JSONB` | `NVARCHAR(MAX)` | `TEXT` |
| `Xml` | `LONGTEXT` | `LONGTEXT` | `XML` | `XML` | `TEXT` |
| `DateTime` | `DATETIME` | `DATETIME` | `TIMESTAMP` | `DATETIME2` | `TEXT` |
| `DateTimeOffset` | `TIMESTAMP` ⚠️ | `TIMESTAMP` ⚠️ | `TIMESTAMPTZ` | `DATETIMEOFFSET` | `TEXT` |
| `Date` | `DATE` | `DATE` | `DATE` | `DATE` | `TEXT` |
| `Time` | `TIME` | `TIME` | `TIME` | `TIME` | `TEXT` |
| `Binary(n)` | `VARBINARY(n)` | `VARBINARY(n)` | `BYTEA` | `VARBINARY(n)` | `BLOB` |
| `Blob` | `LONGBLOB` | `LONGBLOB` | `BYTEA` | `VARBINARY(MAX)` | `BLOB` |
| `Uuid` | `CHAR(36)` | `CHAR(36)` | `UUID` | `UNIQUEIDENTIFIER` | `TEXT` |

### 3.3 参数规则

| 逻辑类型 | 必填参数 | 各方言取值 |
| --- | --- | --- |
| `Decimal` | `precision` + `scale` | MySQL/MariaDB/SS 输出 `(p,s)`；PG `NUMERIC(p,s)`；**SQLite 只出 `NUMERIC`**（存储类无精度约束） |
| `FixedString` / `String` | `length` | Parser 强制必填，避免默认值歧义；SQLite 忽略长度，统一 `TEXT` |
| `Binary` | `length` | MySQL/MariaDB/SS 输出 `VARBINARY(n)`；**PG / SQLite 无长度概念**，统一 `BYTEA` / `BLOB` |
| `Blob` | 无 | 写 `length` 时忽略并告警 `W005` |
| `Uuid` | 无 | 一期不生成 `DEFAULT UUID()`，UUID 由应用层生成写入 |

### 3.4 语义差异（映射表之外必须知道的事）

| 主题 | 结论 |
| --- | --- |
| 时间与时区 | `DateTime` 不带偏移；`DateTimeOffset` 带偏移。MySQL/MariaDB 的 `TIMESTAMP` 存 UTC、按会话时区转换、**不存原始偏移**且有 2038 上限（`W003`）；PG `TIMESTAMPTZ` 同样存时刻而非偏移；只有 SQL Server `DATETIMEOFFSET` 真正存偏移；SQLite 用 `TEXT` 存 ISO8601，格式一致性由应用层保证 |
| JSON | MySQL `JSON`、PG `JSONB` 原生；MariaDB 用 `LONGTEXT` + `JSON_VALID` CHECK；SS/SQLite 为 `NVARCHAR(MAX)` / `TEXT`（`W004`） |
| XML | 只有 PG / SS 原生 `XML`；MySQL/MariaDB `LONGTEXT`、SQLite `TEXT`，编译器输出信息性注释 |
| 无符号整数 | 不设独立逻辑类型，用 `unsigned="true"` 属性；只有 MySQL/MariaDB 生效，其余方言忽略并给 `W001` |
| 自动更新时间 | `onUpdate` 只有 MySQL/MariaDB 支持；其余方言忽略并给 `W002` |
| 布尔值 | MySQL/MariaDB `TINYINT(1)`、SS `BIT`、PG `BOOLEAN`、SQLite `INTEGER`；默认值 `true/false` 由方言格式化为 `1/0`（PG 覆写为 `true/false`） |
| 默认值 | 字符串字面量加引号；函数形态（`CURRENT_TIMESTAMP` / `NOW()` / `utc_timestamp()` / `datetime('now')`）原样透传，不误加引号 |

### 3.5 逻辑类型 ↔ 编程语言（反扫与代码生成用）

| 逻辑类型 | C# | Java | Go | TypeScript |
| --- | --- | --- | --- | --- |
| `Boolean` | `bool` | `boolean` | `bool` | `boolean` |
| `Int8` / `Int16` / `Int32` | `sbyte` / `short` / `int` | `byte` / `short` / `int` | `int8` / `int16` / `int32` | `number` |
| `Int64` | `long` | `long` | `int64` | `bigint` |
| `Float32` / `Float64` | `float` / `double` | `float` / `double` | `float32` / `float64` | `number` |
| `Decimal` | `decimal` | `BigDecimal` | `decimal.Decimal` | `string` |
| `FixedString` / `String` / `Text` | `string` | `String` | `string` | `string` |
| `Json` | `string` / `JsonDocument` | `String` / `JsonNode` | `json.RawMessage` | `unknown` |
| `Xml` | `string` / `XDocument` | `String` / `Document` | `string` | `string` |
| `DateTime` / `DateTimeOffset` | `DateTime` / `DateTimeOffset` | `LocalDateTime` / `OffsetDateTime` | `time.Time` | `Date` |
| `Date` / `Time` | `DateOnly` / `TimeOnly` | `LocalDate` / `LocalTime` | `time.Time` | `Date` |
| `Binary` / `Blob` | `byte[]` | `byte[]` | `[]byte` | `Uint8Array` |
| `Uuid` | `Guid` | `UUID` | `uuid.UUID` | `string` |

这张表只服务反向工程与代码生成，**内核正向编译不依赖它**。

### 3.6 映射表是数据，不是代码

`Common/Resources/dialects/<dialect>.types.xml`（内嵌资源，随程序集发布）：

```xml
<types dialect="mysql">
  <type logical="Boolean" sql="TINYINT(1)" />
  <type logical="Decimal" sql="DECIMAL({precision},{scale})" />
  <type logical="String" sql="VARCHAR({length})" />
  <type logical="DateTimeOffset" sql="TIMESTAMP" />
  ...
</types>
```

新增一种数据库时，类型映射只需新增一份 XML + 一个方言类，`SchemaCompiler` 与 `TypeMapper` 都不改。

---

## 四、执行升级链路（逻辑模型 → SQL → 数据库）

### 4.1 五阶段总览

```
 migrations/*.xml
      │
 ① 解析       ChangeUnitLoader / ChangeUnitParser
      │       - 文件名与 id 一致性、release 格式、属性合法性
      │       - checksum = SHA-256(文件原始字节)
      │       - 按 (release, change_id) 排序
      ▼
 ChangeUnit[]（操作对象树）
      │
 ② 状态推进   StructureModelBuilder
      │       - 回放变更链到当前单元，得到"变更前/变更后"结构
      │       - 供 incrementalModify 补全 MODIFY 未写属性
      ▼
 StructureModel
      │
 ③ 编译       SchemaCompiler + ISqlDialect + TypeMapper + Splitter
      │       - 展开通配符 → 排序 → 逐操作分派 → 方言渲染
      │       - 产出 StatementGroup[]（按表分组）与 CompileResult
      ▼
 SQL 文本 / 语句组 / 诊断
      │
 ④ 输出       文件模式 / --split / --emit pipe
      ▼
 ⑤ 执行       SchemaExecutor + IDbExecutor + IDbLock
             - 方言一致 → 取锁 → checksum 校验 → 执行 → 写状态 → 审计
             ▼
       目标数据库 + version_log/
```

### 4.2 阶段①：解析

输入是目录下所有 `*.xml`，每个文件 = 一个 `ChangeUnit`：

| 解析/校验项 | 处理 |
| --- | --- |
| `id` | `yyyyMMdd_HHmmss[_后缀]`；与文件名前缀一致（`check` 校验） |
| `release` | `yyyyMMdd`；`check` 校验格式、未来日期、与 id 日期一致；同 release 跨天提交给警告 |
| `author` / `description` | 原样进入审计日志 |
| 操作节点 | `table` / `column` / `primaryKey` / `index` / `foreignKey` / `uniqueConstraint` / `checkConstraint` / `defaultConstraint` / `view` / `sequence` / `seed` / `update` / `delete` / `clean` / `backfill` / `dataMigration` / `rawSql` 等 24 个操作类 |
| checksum | `SHA-256(文件原始字节)`，不做换行/BOM 规范化 |

解析结果不可变：文件一旦被应用，checksum 与状态文件绑定，改动即拒绝执行。

### 4.3 阶段②：结构状态推进

`StructureModelBuilder.Build(orderedUnits, upToChangeId)` 从基线开始回放每个单元的操作，得到该单元执行前的结构模型。

两个用途：

1. **增量 `MODIFY` 补全**：XML 里只写 `nullable="false"` 时，编译器从模型里取该列的当前类型/长度/默认值/注释补齐整列（MySQL `MODIFY COLUMN` 是整列替换，不补就丢属性）。
2. **SQLite rebuild 的目标结构**：需要"变更后的完整列定义"才能建新表。

补不到基底（新项目、外部表）时按列退化并给 `W014`，不阻断编译。

### 4.4 阶段③：编译

`SchemaCompiler.Compile(unit)` 的执行顺序：

1. **展开通配符**：`table="*"` 需要库表清单（离线不可用，由执行器注入）。
2. **收集 rebuild 表**：SQLite 下判定哪些操作触发 rebuild。
3. **排序**：表（首次出现序）→ 操作类型优先级 → 书写顺序；`stage="before"` 提到最前，`rawSql` 与无表操作排最后。
4. **逐操作分派**：`Dispatch(operation)` → `dialect.AddColumn(op)` / `dialect.CreateIndex(op)` …；方言不支持时由 `SqlDialectBase` 产出 `E0xx` / `W0xx`。
5. **语句组装填**：每条语句挂到所属 `StatementGroup.Table`，同时记录 `Index` 与 `Hash`（去首尾空白 + 统一 LF 后计算，用于断点续跑比对）。
6. **rebuild 插入**：SQLite 在正确的排序位置插入"建临时表 → 导数据 → 删旧 → 改名"四步。
7. **拼装脚本**：按配置加文件头注释、单元头、诊断注释。

产物：

```csharp
CompileResult
 ├─ Sql          // 完整脚本（人读、落盘）
 ├─ Statements   // 扁平语句列表（逐条执行）
 ├─ Groups       // 按表分组的语句（按表提交 / 失败定位）
 └─ Diagnostics  // E/W 诊断（四处传播：CLI / SQL 头 / 审计 md / check）
```

**编译器是纯函数**：同样的输入 + 方言 + 选项，输出完全确定；不连数据库、不读时钟、不写文件，因此可以在 CI 里对 SQL 做快照测试。

### 4.5 阶段④：三种输出形态

| 形态 | 命令 | 产物 | 适用 |
| --- | --- | --- | --- |
| 文件 | `compile` | `output/<dialect>.sql` | 人工评审、归档 |
| 逐语句文件 | `compile --split --output-dir <dir>` | `<changeId>.<dialect>.sql`（每行一条）+ `_units.txt` | 外部程序逐行执行 |
| 管道 | `compile --emit pipe` / `--pipe <name>` | stdout / 命名管道，分隔符文本 | 中间件启动链路（零落盘） |

管道协议（`DbChronicle.Piping`）：

| 记录 | 字节形态 | 含义 |
| --- | --- | --- |
| 版本 `V` | `0x01 V 0x1F 2` | 第一条，必须是已知版本 |
| 单元开始 `U` | `0x01 U 0x1F <unitId> 0x1F <release>` | 后续 SQL 属于该单元 |
| SQL | 裸文本（无前缀） | 已去首尾空白与结尾分号，保留换行与内部注释 |
| 单元结束 `E` | `0x01 E` | 调用方在此提交事务 |
| 诊断 `D` | `0x01 D 0x1F <severity> 0x1F <code> 0x1F <message>` | 编译错误/告警 |
| 流结束 `Z` | `0x01 Z` | 必须收到，否则判为截断 |

顺序固定 `V → U → (D* | SQL*) → E → … → Z`；记录分隔符 `0x1E`、字段分隔符 `0x1F`、控制前缀 `0x01`，三者都是 XML 1.0 禁止字符，生成器不会产出，因此**无需转义**。

### 4.6 阶段⑤：执行（升级编排）

`SchemaExecutor.ApplyWith(executor, units, options)`：

```
1. 方言一致性           executor.DialectName vs 配置方言（mysql/mariadb 互通）→ 不符退出码 5
2. 库级互斥锁           PG 咨询锁 / MySQL GET_LOCK / SS sp_getapplock / SQLite 文件锁
                        拿不到 → 退出码 4（锁覆盖"读状态→执行→写状态"整段）
3. 准备状态目录         version_log/{environment}/
4. 读状态 + 校验        已应用单元 checksum 必须一致；记录存在但文件丢失 → 退出码 3
5. 逐单元（按 release, change_id）
     ├─ 已应用且 checksum 一致 → 跳过执行（仍编译，用于汇总 SQL/诊断）
     ├─ 编译 → 有 Error 则中止（退出码 1）
     └─ 执行单元
6. 写状态               事务方言：语句全成功后、提交前写；写失败回滚并删记录
                        无事务方言：每条语句成功后立即写 <statement index hash>
7. SQL 快照             version_log/{env}/sql/{release}/{changeId}.{dialect}.sql
8. 审计日志             version_log/{env}/{release}_dbupdate.md（追加）
9. finally 释放锁
```

两种执行模型：

| 方言 | 模型 | 失败后 |
| --- | --- | --- |
| PG / SQL Server / SQLite | 整单元一个事务；`commitScope=table\|batch` 时按表/按批独立提交 | 回滚 + 删状态记录，可原样重跑 |
| MySQL / MariaDB | 语句级断点续跑 | 写 `failureAt/failureStmt`；重跑按 `index + hash` 跳过已成功语句；hash 不一致拒绝续跑（提示 `--restart`） |

`--continue-on-error` 默认值按方言决定：无事务 DDL 方言**默认继续**（反正是无法回滚，能做完的先做完），事务方言默认停止。失败明细逐行输出，便于贴进工单。

### 4.7 端到端示例

输入（逻辑模型，一份文件）：

```xml
<changeUnit id="20261022_100000_a1b2c3" author="zhangsan" release="20261022">
  <description>用户表增加手机号与启用标记</description>
  <table name="nor_user" action="ALTER">
    <column name="phone" action="ADD" type="String" length="20" nullable="true" comment="手机号"/>
    <column name="is_active" action="ADD" type="Boolean" nullable="false" defaultValue="true"/>
    <index name="idx_user_phone" action="ADD" columns="phone"/>
  </table>
</changeUnit>
```

编译输出（MySQL）：

```sql
-- ChangeUnit: 20261022_100000_a1b2c3
ALTER TABLE `nor_user` ADD COLUMN `phone` VARCHAR(20) NULL COMMENT '手机号';
ALTER TABLE `nor_user` ADD COLUMN `is_active` TINYINT(1) NOT NULL DEFAULT 1;
CREATE INDEX `idx_user_phone` ON `nor_user` (`phone`);
```

编译输出（PostgreSQL）：

```sql
-- ChangeUnit: 20261022_100000_a1b2c3
ALTER TABLE "nor_user" ADD COLUMN "phone" VARCHAR(20) NULL;
COMMENT ON COLUMN "nor_user"."phone" IS '手机号';
ALTER TABLE "nor_user" ADD COLUMN "is_active" BOOLEAN NOT NULL DEFAULT true;
CREATE INDEX "idx_user_phone" ON "nor_user" ("phone");
```

编译输出（SQL Server）：

```sql
-- ChangeUnit: 20261022_100000_a1b2c3
ALTER TABLE [nor_user] ADD [phone] NVARCHAR(20) NULL;
ALTER TABLE [nor_user] ADD [is_active] BIT NOT NULL CONSTRAINT [DF_nor_user_is_active] DEFAULT 1;
CREATE INDEX [idx_user_phone] ON [nor_user] ([phone]);
```

编译输出（SQLite）：

```sql
-- ChangeUnit: 20261022_100000_a1b2c3
ALTER TABLE "nor_user" ADD COLUMN "phone" TEXT NULL;
ALTER TABLE "nor_user" ADD COLUMN "is_active" INTEGER NOT NULL DEFAULT 1;
CREATE INDEX IF NOT EXISTS "idx_user_phone" ON "nor_user" ("phone");
```

执行后落盘：

```
version_log/dev/
├── DbchronicleState.xml                 ← changeId / checksum / appliedAt / appliedBy
├── 20261022_dbupdate.md                 ← 执行人、库信息、单元清单、警告、SQL
└── sql/20261022/
    └── 20261022_100000_a1b2c3.mysql.sql ← 实际执行的语句快照
```

再执行一次 `upgrade`：checksum 一致 → 跳过执行，输出"全部已应用"（幂等）。

### 4.8 链路各阶段失败点与退出码

| 阶段 | 失败场景 | 结果 |
| --- | --- | --- |
| 解析 | XML 格式错、id/release 非法 | 退出码 1，不产生任何状态 |
| 检查 | 语义冲突、`--strict-warnings` 下存在 W | 退出码 1（CI 拦截） |
| 编译 | 方言不支持（E046/E047/E044…） | 退出码 1，SQL 不落盘 |
| 取锁 | 另一进程正在升级 | 退出码 4，不重试 |
| 校验 | 已应用文件被改 / 文件丢失 | 退出码 3，拒绝执行 |
| 执行（事务方言） | 任一语句失败 | 回滚 + 删状态记录，退出码 3，可重跑 |
| 执行（MySQL） | 第 N 条失败 | 记录 `failureStmt`，输出已成功条数；重跑从断点继续 |
| 审计 | md / 快照写失败 | 只警告，不阻塞升级，下次 `--backfill-logs` 补 |

---

## 五、现状与边界

| 项 | 现状 |
| --- | --- |
| 测试 | net8.0 全量 273 通过 / 0 失败（核心 189 + Scanner 7 + Cli 42 + Integration 35） |
| 内核规模 | `Src/DbChronicle/` 120+ 源文件；`Models/` 26 个模型 + 24 个操作类 |
| 方言 | 5 种（MySQL / MariaDB / PostgreSQL / SQL Server / SQLite） |
| 操作 | 13 类，方言不支持时显式报错或告警，不静默丢弃 |
| 输出 | 文件 / 逐语句文件 / 管道协议三种 |
| 状态 | 本地 `DbchronicleState.xml`，不建版本表；多实例需共享目录 |
| 已明确不做 | 全量快照 diff、运行时自动迁移、独立服务、完整反向操作推导 |
| 仍需人工 | DROP 类与数据操作的回滚（只出 blocker）、MySQL 半态的人工确认（`--mark-applied`）、大表 ALTER 的锁时长评估 |

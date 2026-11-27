# DbChronicle 升级链合法性检查方案

> 定位：本文是《DbChronicle — 数据库版本托管与升级工具集 方案文档》的专题补充稿，展开 §4.2（操作顺序）、§4.4（原破坏性分级）、§4.7.2（降级矩阵）与 §5.10（累积模型）。
> 权威性：术语、诊断码、执行语义以主方案文档为准；本文冲突时以主文档为准，并应回改主文档。
> 版本：v2（2026-11-25，由 v1「结构破坏性变更」修订而来；2026-11-27 增补 §2.2 两条正交轴）
>
> 变更（2026-11-26）：主方案已移除 `allowDestructive` 开关，E030 不再产生；本文已按"直接执行 + 审计/备份"口径同步修订，L1–L6 检查规则不变。

---

## 摘要

1. **"破坏性"不是一类操作，而是一类判定结果。** 上位概念是**升级链的合法性**：一条变更能否在"它被执行时所处的结构状态"上被无歧义、可执行地应用。破坏性（丢数据/改写数据/破坏引用）只是合法性判定中的"后果层"，不是独立体系。
2. 用户举的例子点出了当前最大的缺口：**要修改某列属性，前提是该列必须存在**；否则生成的 `ALTER TABLE … MODIFY COLUMN` 是非法 SQL。当前实现对这件事**既不报错也不告警**，还会把"改一个不存在的列"静默当成"新增该列"。
3. 合法性分六层：**形式 → 引用存在性 → 时序 → 一致性 → 后果 → 方言可行性**。前四层决定"这条变更是否合法"，第五层决定"这条合法变更多危险"，第六层决定"这个方言能否落地"。
4. 合法性检查的输入不是单条 SQL，而是 **`S(i-1)`（变更前累积状态）+ `U_i`（本单元声明）**。因此它天然是"升级链级"检查，必须沿链推进，不能孤立看单个文件。
5. 校验绝不引入新的落盘开销：实测状态文件单次写入 15–35 ms，N=1000 累计 111–133 s，任何"每次检查写结果"的设计都会放大该瓶颈。
6. **整套机制只承担两个职责**：**A 可执行性**（这条 SQL 能否正常执行完）与 **B 历史无影响**（执行完后历史数据和既有引用是否还正确）。六层模型是这两个职责的展开；门禁、探针、备份三类处置手段也按这两个职责分派——**A 不可豁免，B 可豁免**（见 §2.1）。
7. **跨方言版本升级会静默丢产物**：同一份 XML 在不同数据库版本下产物不同（5.7 用 `CHANGE`、8.0 用 `RENAME COLUMN`），而状态文件只记方言不记版本、SQL 快照名不含版本、降级诊断不落账。需「版本入账 + 执行前断言 + 升级审计 + 降级台账」四件套（见 §13）。
8. **发布版本漏提交会静默丢迭代**：`release=20261123` 的变更集在 20261123 发布之后才提交，现有校验只有一条不分方向的 W012 警告，系统也不知道"这个 release 已经发布过"。需三层防线：静态迟交规则（E121）+ 封版清单（E122）+ 运行期版本空洞检测（E123）（见 §14）。
9. **基线种子可作为新起点**：每个 release 成功后把真实结构固化为种子，后续只回放「种子 + 增量」，可消除长链回放成本；但种子必须是**库观测结果**且**drift 归零**才允许固化，且检查仍须在全链上做（见 §15）。
10. **六层模型只覆盖结构合法性**：版本号升级链是另一条正交轴，归入链上下文合法性 C1–C3（依赖图、发布版本、方言能力版本），不是 L1–L6 的遗漏（见 §2.2）。

---

# 第一部分：升级链合法性模型

## 1. 概念修正

### 1.1 三个层次不要混

| 概念 | 回答的问题 | 举例 |
|---|---|---|
| **合法性（Legality）** | 这条变更能否被合法应用？ | `MODIFY` 的列是否存在；`ADD` 的列是否已存在；改名后的旧名是否仍被引用 |
| **可行性（Feasibility）** | 目标方言能否实现？ | PG 不支持 `REORDER`（E044）；MySQL 无序列（E046） |
| **后果（Impact）** | 合法且可行，但对既有数据/引用有什么影响？ | 删列丢数据；类型收窄截断；加 NOT NULL 失败 |

原 v1 文档把第三层叫"破坏性"，并把它当成整个体系，**导致第二层被忽略**。实际上"改不存在的字段"比"改字段类型收窄"更基础——前者是 SQL 直接报错，后者是数据悄悄出错。

### 1.2 判定基准：状态而不是文件

合法性检查的对象不是"这个 XML 写得对不对"，而是：

```
在 S(i-1) 上执行 U_i 是否合法 → 得到 S(i)
其中 S(i) = Apply(S(i-1), U_i)
```

由此产生两种粒度，缺失任何一种都会漏：

| 粒度 | 范围 | 典型检查 |
|---|---|---|
| 单元内合法性 | `U_i` 内部操作之间 | 先建表后加索引；先加列后建索引；rename 后同单元必须用新名；`stage="before"` 不得引用本单元新增的列 |
| 链级合法性 | `S(i-1)` → `U_i` | 列是否存在、表是否存在、列是否已被删、索引/约束是否已存在、改名目标是否撞名 |

## 2. 六层合法性模型

| 层 | 名称 | 判定内容 | 诊断码段 | 现状 |
|---|---|---|---|---|
| L1 | 形式合法性 | 节点/属性/枚举/格式/encoding | E001–E053 | 覆盖较好 |
| **L2** | **引用存在性** | 被引用的表、列、索引、约束、外键、序列是否存在且唯一 | **E111–E118（新增）** | **仅 E106 一条（DROP 列），其余大量静默通过** |
| L3 | 时序合法性 | 依赖是否已就绪（建表先于索引、加列先于改列、改名后引用新名、stage=before 边界） | E105 / E109 / E119 | 部分覆盖 |
| L4 | 一致性合法性 | 同一目标的多次变更是否矛盾 | E101–E104 / E110 | 覆盖较好 |
| L5 | 后果合法性 | 对既有数据与既有引用的影响（原"破坏性"） | E054–E056 / W034–W039 | 无破坏性门禁（E030 已移除） |
| L6 | 方言可行性 | 目标方言能否实现 | E041–E053 | 覆盖较好 |

**L2 是本次要补的主体**，L3 补齐，L5 沿用 v1 已设计的规则集。

### 2.1 两个职责：六层其实只回答两件事

> **A. 这条 SQL 能不能正常执行完？**（可执行性）
> **B. 执行完之后，历史数据和既有引用还对不对？**（历史无影响）

| 职责 | 覆盖层 | 判定输入 | 违反后的现象 | 能否豁免 |
|---|---|---|---|---|
| **A 可执行性** | L1 形式、L2 引用、L3 时序、L4 一致性、L6 的 E 类、**L5 的 V-01~V-05** | 只需 `S(i-1)` 结构 | 执行期报错；PG / SQL Server / SQLite 可回滚，MySQL 留半态 | **不能**——非法 SQL 不会因为业务需要而变合法 |
| **B 历史无影响** | L5 的 D / B / S 类、V-06、L6 的 W 类（意图丢失） | 结构 + （V 类）数据分布 | 执行成功，但数据或引用被改坏；不可逆 | **能**——业务确实要删列/收窄时直接执行（原 `allowDestructive` 已移除） |

**关键推论**：

1. **门禁不能共用一个开关**。原 `allowDestructive` 只属于 B，且已于 2026-11-26 移除；A 类仍必须硬失败，没有放行理由。
2. **判定成本不同**。A 类纯离线 O(1)；B 类中只有 V-06 需要探针，D/B/S 类纯结构可判。
3. **失败代价不同**。A 类失败有事务兜底（MySQL 除外），修好 XML 重跑即可；B 类失败无法回滚，前置动作必须是备份。
4. **测试方式不同**。A 类用"构造非法链 + 断言报错"；B 类用"构造数据分布 + 探针"或人工确认。
5. **诊断码要标职责**。CI 里 A 类必须 error，B 类可按环境分级。

**L5 的 V 类要按职责再拆开**——这是原 v1 文档的一处误归类：

| V 类 | 数据不满足时 | 实际职责 |
|---|---|---|
| V-01 ~ V-05（NOT NULL / 唯一 / 主键 / 外键 / CHECK 收紧） | **执行失败**，不改坏已有值 | **A**（只是离线判不了，用探针提前发现；探针命中应中止，不是警告） |
| V-06（`Text → String(len)` 长度不足） | 超长值被截断 | **B**（数据被改） |

即：V-01~V-05 本质是"**A 的运行时失败**在离线阶段的投影"。因此 v1.1 的 `verifyDataBeforeTighten` 只该管 V-01~V-05，命中即中止执行；而 V-06 走 B 的备份 + 放行语义。

**同时命中两类的操作**（说明两个职责彼此独立，不能互相替代）：

| 操作 | A | B |
|---|---|---|
| `MODIFY` 不存在的列 | ✗ 生成非法 SQL | — |
| `DROP` 存在但不为空的列 | ✓ | ✗ 丢数据 |
| `ADD` 已存在的列 | ✗ 重复列 | — |
| `Decimal(18,2) → (20,8)` | ✓ | ✗ 整数位收窄 |
| 加 `NOT NULL` 但存在 NULL 行 | ✓ 语法合法（运行时失败） | — |
| 非 MySQL 方言改 `unsigned` | ✓ | ✗ 约束意图丢失（W001） |

### 2.2 两条正交轴：结构合法性与链上下文合法性

L1–L6 回答的是「在 `S(i-1)` 上执行 `U_i`，这一步本身是否合法」，输入是累积结构模型与当前变更单元；版本号升级链回答的是「这一步是否属于它声称的发布版本与数据库能力级别」，真值来源是发布记录、依赖图、状态文件与编译快照。两者不是包含关系，不能把版本号链硬塞成 L1–L6 的某一层。

因此整套检查应按两条轴组织：

**第一轴：结构合法性 L1–L6**（本文 §1–§12 的主体）

| 层 | 判定对象 | 真值来源 |
|---|---|---|
| L1 | 单节点形式 | XML 语法与枚举 |
| L2 | 被引用对象是否存在且唯一 | `S(i-1)` 累积模型 |
| L3 | 依赖对象是否已就绪、改名后是否用新名 | `S(i-1)` + 单元内操作顺序 |
| L4 | 多次变更是否矛盾 | `S(i)` 推导字典 |
| L5 | 对历史数据与引用的影响 | `S(i-1)` 与 `S(i)` diff |
| L6 | 目标方言能否实现 | 方言能力矩阵 |

**第二轴：链上下文合法性 C1–C3**

| 类 | 名称 | 判定内容 | 真值来源 | 诊断码 | 对应章节 |
|---|---|---|---|---|---|
| C1 | 依赖图自洽 | `depends` 指向存在、补丁依赖、跨单元顺序 | 单元自身声明 | E107 / E108 / E109 | §4、§14.8 |
| C2 | 发布版本链 | 迟交、封版清单、清单外单元、checksum 变化、运行期版本空洞、链头锚定 | `release` / `id` / 封版清单 / 状态文件 | E121–E124 / W046 / W047 | §14 |
| C3 | 方言能力版本链 | 能力级别漂移、同一单元在不同版本下 SQL 产物变化、历史降级项残留 | `capabilityLevel` / SQL 快照 / 降级台账 | E120 / W043–W045 | §13 |

要点：

- C1 与 L3 有交叉但含义不同：L3 判断「被引用的结构对象是否已就绪」，C1 判断「已声明的依赖图本身是否自洽」；图自洽不等于发布完整。
- C2 判断「这张图是不是发布时那张完整图」。`depends` 只能证明图自洽，不能证明图完整；完整性必须由封版清单或链头锚定这类外部事实提供。
- C3 判断「执行上下文与生成产物时的能力级别是否一致」。同一 XML 在 MySQL 5.7 与 8.0 下产物不同，checksum 不变不代表产物不变。
- C 类检查不进入 `S(i)` 推进，作为旁路门禁与 L1–L6 并行运行；其诊断与结构层诊断在传播阶段合并。

## 3. L2：引用存在性（前置条件）

### 3.1 核心原则

> 任何操作如果引用了对象，该对象必须在 `S(i-1)` 中**存在**；如果操作会创建对象，该对象必须**不存在**。

违反应当报 Error，理由与"破坏性"无关——**生成的 SQL 本身就是非法 SQL**，执行期必然失败；在 MySQL 上还会因为无事务 DDL 留下半态（主文档 §6.7）。

### 3.2 当前实现的失效模式（已核实）

`StructureModelBuilder.ApplyColumn` 面对"表不存在"和"列不存在"时，选择了**静默容错**：

```169:198:Src/DbChronicle/Models/StructureModelBuilder.cs
        private void ApplyColumn(StructureModel model, ColumnOperation operation)
        {
            TableModel table;
            if (!model.Tables.TryGetValue(operation.Table, out table))
            {
                if (operation.Action != ColumnAction.Add)
                {
                    return;
                }
                table = new TableModel(operation.Table);
                model.Tables[operation.Table] = table;
            }
            ...
                case ColumnAction.Modify:
                    ColumnDefinition existing;
                    if (table.Columns.TryGetValue(operation.Name, out existing))
                    {
                        table.SetColumn(ColumnMerger.Merge(existing, operation.Definition));
                    }
                    else
                    {
                        table.SetColumn(operation.Definition.Clone());
                    }
```

由此产生两种失败形态：

| 形态 | 触发 | 结果 |
|---|---|---|
| **静默忽略** | 表不存在时的 `MODIFY` / `DROP` / `RENAME` / `COMMENT` / 主键操作；`DefaultConstraint` 的表或列不存在 | 模型不变，编译照过，SQL 生成后执行期报"表/列不存在" |
| **隐式建表 / 隐式加列** | 表不存在时的 `ADD`、`FOREIGN KEY`、`CHECK`；列不存在时的 `MODIFY` | 模型凭空多出表/列，后续单元基于这个虚假状态继续推导，错误被放大 |

这两种形态的共同后果：**`check` 通过，问题延迟到生产执行**。这比"破坏性未拦截"更难排查，因为它连"执行失败"都不一定给出（MySQL 半态）。

其余静默点一览（同一文件）：

| 操作 | 行 | 行为 |
|---|---|---|
| `RENAME TABLE` 源不存在 | 70–77 | 静默 return |
| 主键 `ADD/DROP` 表不存在 | 84–91 | 静默忽略 |
| 表注释 表不存在 | 144–147 | 静默忽略 |
| 默认值约束 表/列不存在 | 154–165 | 静默忽略 |
| 外键 / CHECK 表不存在 | 98–106 / 121–129 | **隐式建表** |

### 3.3 前置条件矩阵

判定基准：`S(i-1)`。

#### 表级

| 操作 | 必须成立 | 违反诊断 | 现状 |
|---|---|---|---|
| `CREATE` | 该表在 `S(i-1)` 中不存在 | E111 重复建表 | 无检查，模型直接覆盖 |
| `ALTER`（容器） | 表存在 | E111 | 无检查 |
| `DROP` | 表存在（原 E030 只管授权，已于 2026-11-26 移除） | E111 | 无检查 |
| `RENAME` | 源表存在；目标名未被占用 | E117 / E104 | 源存在未查 |
| `COMMENT` / `ENGINE` / `CHARSET` / `TABLESPACE` | 表存在 | E111 | 静默忽略 |

#### 列级

| 操作 | 必须成立 | 违反诊断 | 现状 |
|---|---|---|---|
| `ADD` | 表存在（或本单元刚 CREATE）；列**不**存在 | E111 / **E113** | E101 只比 `ADD` vs `ADD`，**不含 `CREATE TABLE` 里的列**；表不存在时隐式建表 |
| **`MODIFY`** | **表存在；列存在** | E111 / **E112** | **完全没有**，静默变成"新增该列" |
| `DROP` | 列存在 | E106 | 已覆盖 |
| `RENAME` | 源列（`renamedFrom`）存在；目标名未被占用 | **E117** / E104 | 源存在未查 |
| `REORDER` | 列存在；`after` 指定的列也存在（`FIRST` 除外） | **E112** | 未查 |
| `COMMENT` | 列存在 | E112 | 静默忽略 |

#### 主键 / 索引 / 外键 / 约束

| 操作 | 必须成立 | 违反诊断 | 现状 |
|---|---|---|---|
| 主键 `ADD` | 表存在；所有 `columns` 存在；表当前无主键；自增列不参与复合主键（SQLite） | E111 / **E112** / **E114** | 表与列存在性均未查；SQLite 复合主键已有编译期异常 |
| 主键 `DROP` / `REPLACE` | 表存在；当前有主键；新列存在 | E111 / E114 / E112 | 未查 |
| 索引 `ADD` | 表存在；所有 `columnRef` 存在；索引名在本表未被占用 | E111 / E112 / **E115** | 未查 |
| 索引 `DROP` / `RENAME` | 索引存在；改名目标未被占用 | **E115** / E115 | 未查（模型不跟踪索引） |
| 外键 `ADD` | 本表存在；本表列存在；`refTable` 存在；`refColumns` 存在且是目标表的主键/唯一列 | E111 / E112 / **E116** | 全部未查，表不存在时隐式建表；E109 只查"引用更晚创建的表" |
| 外键 `DROP` / `MODIFY` | 外键存在 | E115 | 未查 |
| 唯一 / CHECK / 默认值约束 `ADD` | 表存在；列存在；约束名未占用 | E111 / E112 / E115 | 未查 |
| 约束 `DROP` | 约束存在 | E115 | 未查 |

#### 数据节点

| 操作 | 必须成立 | 违反诊断 | 现状 |
|---|---|---|---|
| `update` / `delete` / `clean` / `backfill` | 表存在；`set` / `rules` / `column` 中出现的列存在 | E111 / E112 | 未查 |
| `update` / `clean` / `backfill` | 必须显式声明 `where`（`where="1=1"` 表示全表） | **E056** | 未查 |
| `seed` | 表存在；`columns` 中的列存在；每行值个数 = 列个数 | E111 / E112 / **E118** | 未查 |
| `dataMigration` / `rawSql` | 无法结构化校验 | W039 提示 | — |

#### 视图 / 序列

| 操作 | 必须成立 | 违反诊断 | 现状 |
|---|---|---|---|
| `view` `REPLACE` / `DROP` | 依赖表存在（`<select>` 内无法可靠解析，只能提示） | W 提示 | 未查 |
| `sequence` `MODIFY` / `DROP` | 序列存在 | **E115** | 模型未跟踪序列；E046 只管方言 |

### 3.4 L2 实现方式

不新增一套解析器，而是让 `StructureModelBuilder` 在应用每个操作时**同时产出解析结果**：

```
ResolveResult { Found | NotFound | Created }
```

三种去向：

| 情况 | 处理 |
|---|---|
| `Found` | 正常应用，模型更新 |
| `NotFound` 但操作允许创建（`CREATE`、`ADD` 列到本单元刚建的表） | 正常应用 |
| `NotFound` 且操作要求存在 | **记录诊断（E111–E118），仍然应用（容错以便一次报出全部问题），但编译标记为失败** |

要点：
- **不要直接抛异常**，否则一条错误会掩盖后续所有错误，与现有 "一次报全" 的体验冲突；
- **不要保留隐式建表**，改为"记录诊断 + 在模型中留占位"，避免虚假状态污染后续单元推导；
- 键名统一大小写不敏感（与现有字典一致）。

## 4. L3：时序合法性补齐

| 规则 | 触发 | 诊断 | 现状 |
|---|---|---|---|
| 建表先于索引/FK | 同单元内对表 `T` 加索引/FK 时，`T` 尚未 CREATE 且不在 `S(i-1)` | E111（由 L2 自然覆盖） | — |
| 改名后引用新名 | 同单元内 `RENAME` 之后仍用旧名 | E105 | 已覆盖 |
| 改名目标唯一 | 目标名未被历史使用 | E104 | 已覆盖（列），表未覆盖 |
| `stage="before"` 边界 | 数据前置段引用本单元新增的表/列 | **E119** | 文档 §4.2 已写明，未实现 |
| 跨单元依赖顺序 | 引用更晚创建的表；引用已删除的表 | E109 | 已覆盖 |
| 补丁依赖声明 | 补丁文件缺 `depends` | E108 | 已覆盖 |

## 5. L5：后果合法性（原"破坏性"部分，保留）

### 5.1 四维分类

| 类别 | 定义 | 判定依据 | 默认处置 |
|---|---|---|---|
| **D — Destructive** | 既有数据行/值不可恢复 | 纯结构 diff | Error（直接执行；原门禁已移除） |
| **V — Data-Dependent** | 是否失败取决于库里现有数据 | 必须探针 | Warning + 探针 SQL |
| **B — Backward-Incompatible** | 数据不丢，但旧引用断裂 | 纯结构 diff | Warning（CI 可升 Error） |
| **S — Semantic Drift** | 能跑但语义被弱化 | 方言能力矩阵 | Warning（复用 W001–W033） |

### 5.2 五类破坏行为（按“对历史数据的影响方式”）

| 类别 | 代表操作 | 特征 |
|---|---|---|
| 直接销毁 | `DROP TABLE` / `DROP COLUMN` / `<delete>` / `rawSql` 内 `TRUNCATE` | 不可逆，无事后补救 |
| 静默改写 | 类型收窄、长度缩短、精度缩减、`unsigned` 翻转、字符集/排序规则变更、`DateTimeOffset→TIMESTAMP`、SQLite rebuild 的 `CAST` | 执行成功、行数不变、无报错，事后对账才发现 |
| 约束收紧 | `nullable="false"`、`unique`、加主键、加外键、加 CHECK、`Text→String(len)` | 不毁数据，但既有数据不满足时执行中途失败（MySQL 留半态）；MySQL 5.7 的 CHECK 甚至不生效（W023） |
| 身份与引用断裂 | 重命名表/列、删/换主键、改自增、SQLite rebuild | 数据在，但视图/触发器/过程/代码里的旧引用静默失效；自增重置会让未来插入撞历史主键 |
| 执行方式放大 | MySQL 无事务 DDL、`rollback` 对 DROP/数据操作标 blocker、大表 ALTER 锁 | 与单条 DDL 无关，放大前四类的后果 |

### 5.3 专门针对"历史数据更新"的操作清单

| 操作 | 隐蔽度 | 说明 |
|---|---|---|
| `<update set where>` | 高 | 漏写 `where` 即全表覆盖；`set` 写错即整列被同一值填满 |
| `<clean rules where>` | 高 | `rules` 是 `SET` 赋值串，`;` 转逗号，写错批量置空 |
| `<backfill column value>` | 中 | 本意补空值，漏 `where` 会覆盖已有值 |
| `<seed conflict="UPDATE">` | **极高** | 用种子值覆盖线上已有行；运维手工调过的配置/文案被静默改回；SQL Server 降级为普通 INSERT + W029，改为主键冲突中断 |
| `<dataMigration>` / `<rawSql>` 内 DML | 极高 | 绕过结构化校验 |
| 默认值变更 / 删除 | 中 | 不改历史行，但造成"历史行 NULL、新行默认值"并存；full 模式漏写会清空（W008） |
| `onUpdate` 变更 | 中 | 改变既有行下次被更新时的时间戳行为；非 MySQL 忽略（W002） |
| 自增 / 主键变更 | 中 | 不改既有行，但改变未来 ID 分配，可能与历史冲突 |
| 字符集 / 排序规则变更 | 中 | 数据字节不变，比较、排序、`LIKE`、唯一性判定结果改变 |
| 时区语义变更 | 高 | 时间值不变，被解释成另一个时刻 |

### 5.4 后果层规则表

| 规则 | 触发 | 码 |
|---|---|---|
| D-01~D-03 | 删表 / 删列 / 删行 | —（E030 已移除） |
| D-04、D-05 | 整数位宽收窄 / 字符·二进制长度缩短 | **E054** |
| D-06 | **精度损失**（定点整数位、小数位、浮点有效位、定点↔浮点、字符集收窄） | **E057 / E058**，见 §5.6 |
| D-07~D-08 | 删主键·换主键 / 改自增 | **E055** |
| D-09 | 重建表 | W018 |
| D-10 | `update`/`clean`/`backfill` 缺 `where` | **E056** |
| D-11 | rawSql 含 `DROP`/`TRUNCATE`/`ALTER…DROP` | **W039** |
| V-01~V-05 | NOT NULL / 唯一 / 主键 / 外键 / CHECK 收紧 | **W034** + 探针 SQL |
| V-06 | `Text → String(len)` | **W035** + 探针 SQL |
| V-07 | 收紧前无 `stage="before"` 回填 | W034 附注 |
| B-01~B-03 | 删表/删列附注、重命名表/列 | **W036** |
| S-01 | 类型族改变（`DateTime→Date`、`Decimal→Int`…） | **W037** |
| S-02 | 字符集 / 排序规则变更 | **W038** |

### 5.5 位宽与长度判定矩阵（D-04/D-05）

只比逻辑类型，不比物理类型。

| 源 → 目标 | 判定 |
|---|---|
| `Int64→Int32/16/8`、`Int32→16/8`、`Int16→8` | 收窄（存储范围变小） |
| `unsigned` → 有符号 或 反向（位宽不变） | 收窄（负值失败 / 超上限） |
| `String(n)→String(m)`，m<n；`FixedString` 同理 | 收窄（字符数） |
| `Binary(n)→Binary(m)`，m<n | 收窄（二进制截断最难发现） |
| `DateTime→Date` / `DateTime→Time` | 收窄（丢分量） |
| `DateTimeOffset→DateTime` | 收窄（丢偏移）+ W003 |
| `Text→String(n)` | V-06，不直接判 D |
| 同族放宽（`Int32→Int64`、`String(50)→String(100)`） | 安全 |

### 5.6 精度损失专题（D-06）

"精度"不是一个指标，而是六个彼此独立的量。只比 `type` 名字会全部漏判——`Decimal(18,2) → Decimal(20,8)` 类型名没变、precision 还变大了，但整数位从 16 位降到 12 位，是**收窄**。

#### 5.6.1 六个精度维度

| 维度 | 载体 | 变更即损失的情形 |
|---|---|---|
| ① 整数存储范围 | 整数位宽 / `unsigned` | 位宽变小；有符号↔无符号 |
| ② 定点整数位 | `precision - scale` | **差值变小**（与 precision 单独变大无关） |
| ③ 定点小数位 | `scale` | **scale 变小**（四舍五入，原值不可还原） |
| ④ 浮点有效数字 | `Float32`（6–9 位十进制）/ `Float64`（15–17 位） | 有效位下降；**定点↔浮点互转**（浮点是二进制近似，`0.1` 无法精确表示） |
| ⑤ 时间精度 | 小数秒（秒以下位数） | MySQL `DATETIME` 默认 0 位、PG `TIMESTAMP` 默认 6 位、SQL Server `datetime2` 默认 7 位 |
| ⑥ 字符集 / 编码 | `charset` / `collate` | `utf8mb4 → latin1`：不可表示字符被替换为 `?`（非严格模式）或直接失败 |

#### 5.6.2 反直觉案例（评审时最容易被放过的）

| 变更 | 直觉 | 实际 |
|---|---|---|
| `Decimal(18,2) → Decimal(20,8)` | precision 变大 = 放宽 | **收窄**：整数位 16 → 12，大额值溢出 |
| `Decimal(38,0) → Float64` | 数值类型，不会丢 | **收窄**：Float64 只有 15–17 位有效数字，38 位定点整数必然失真 |
| `Float64 → Decimal(18,6)` | 定点比浮点"更精确" | **收窄**：小数位被截到 6 位；且超出 `precision` 的值转换失败（PG 报 numeric overflow） |
| `Int64 → Decimal(18,0)` | 都是整数 | **收窄**：`Int64` 最大 19 位十进制，`Decimal(18,0)` 装不下 |
| `Decimal(10,2) → Decimal(18,4)` | — | **安全**：整数位 8 → 14、小数位 2 → 4，两维都放宽 |
| `Decimal(10,2) → String(20)` | 字符串更长，肯定放得下 | 值不会丢，但排序/比较/计算语义全变 → 归 **S-01**（W037），不归 D |

#### 5.6.3 实现层陷阱（已核实代码）

| 陷阱 | 机制 | 后果 |
|---|---|---|
| **精度默认值** | `TypeMapper.Resolve` 对 `precision`/`scale` 取 `GetValueOrDefault(18)` / `GetValueOrDefault(2)` | `type="Decimal"` 未写 precision/scale 时静默生成 `DECIMAL(18,2)`；若基底列是 `DECIMAL(30,10)`，这就是一次静默收窄 |
| **旧属性残留** | `ColumnMerger.Merge` 只覆盖 `HasValue` 的字段 | 类型族切换（`Decimal → String`）时未写 `length`，会沿用旧的 `Length`（可能为 null）→ 生成 `VARCHAR(0)`；反向 `String → Decimal` 会沿用旧 `Length` 而精度走默认 |
| **时间精度不可表达** | 逻辑类型只有 `DateTime`，映射为固定字符串 `DATETIME` / `TIMESTAMP`，不带小数秒 | 想改 `DATETIME(3) → DATETIME(0)` 无法用结构化节点表达，只能用 `<rawSql>` → 应输出 W040 提示 |
| **PG 二进制无长度** | `Binary` / `Blob` 均映射为 `BYTEA` | 在 PG 上改 `Binary(100) → Binary(20)` 不产生任何物理约束，长度意图丢失（不报错、不生效）→ 应输出 W041 |
| **PG 类型变更判定完整** | `TypeChanged` 同时比较 Type/Length/Precision/Scale/Unsigned | 精度变化会生成 `ALTER COLUMN TYPE`，不会漏生成语句 |

```53:65:Src/DbChronicle/Compilation/Dialects/TypeMapper.cs
        public string Resolve(LogicalType type, int? length, int? precision, int? scale, bool unsigned)
        {
            if (!_map.TryGetValue(type, out var value))
            {
                throw new NotSupportedException("方言 " + DialectName + " 不支持类型：" + type);
            }
            string text = value.Replace("{length}", length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)).Replace("{precision}", precision.GetValueOrDefault(18).ToString(CultureInfo.InvariantCulture)).Replace("{scale}", scale.GetValueOrDefault(2).ToString(CultureInfo.InvariantCulture));
```

```9:33:Src/DbChronicle/Common/ColumnMerger.cs
        public static ColumnDefinition Merge(ColumnDefinition baseDefinition, ColumnDefinition overlay)
        {
            var result = baseDefinition.Clone();
            result.Name = overlay.Name;

            if (overlay.Type.HasValue)
            {
                result.Type = overlay.Type;
            }
            if (overlay.Length.HasValue)
            {
                result.Length = overlay.Length;
            }
            if (overlay.Precision.HasValue)
            {
                result.Precision = overlay.Precision;
            }
            if (overlay.Scale.HasValue)
            {
                result.Scale = overlay.Scale;
            }
```

#### 5.6.4 精度规则表

| 规则 | 触发 | 码 |
|---|---|---|
| P-01 整数位宽收窄 | 整数族位宽变小；`unsigned` 翻转 | **E054** |
| P-02 定点整数位收窄 | `(p2 - s2) < (p1 - s1)`（**不比较 p 本身**） | **E057** |
| P-03 小数位缩减 | `s2 < s1` | **E057** |
| P-04 浮点有效位下降 | `Float64 → Float32` | **E057** |
| P-05 定点 ↔ 浮点互转 | `Decimal → Float*`、`Float* → Decimal`（双向） | **E057**（`>2^53` 的整数→浮点同理） |
| P-06 `Decimal → Int*` | 舍小数 + 可能溢出 | **E057** |
| P-07 整数 → 定点装不下 | `Int64` 需 ≥19 位整数、`Int32` 需 ≥10 位 | **E057** |
| P-08 字符集收窄 | `charset` 从更宽字符集改为更窄（`utf8mb4 → utf8/latin1`） | **E058** |
| P-09 精度未显式声明 | `Decimal` 的 `precision`/`scale` 缺省且基底存在更宽定义 | **W040**（提示将使用 18,2） |
| P-10 时间精度变更 | 声明 `Time`/`DateTime` 且历史定义含小数秒 | **W041**（逻辑模型无法表达，建议 `<rawSql>`） |
| P-11 PG 二进制长度无效 | PG 下 `Binary`/`Blob` 的 `length` 变化 | **W042**（`BYTEA` 无长度约束） |

#### 5.6.5 静态度量的边界

- 精度损失是**可能性判定**：`E057` 的理由是"现有值可能超出新精度"，不是"必然失败"，所以需人工确认并备份后直接执行；
- 真正判定需要探针：`SELECT MAX(ABS(c))`、`SELECT MAX(LENGTH(c))`、`SELECT COUNT(*) WHERE c <> ROUND(c, s)` 属 V 类（W034/W035），归 v1.1；
- 浮点类**无法用探针判定**——`Decimal → Float64` 的失真发生在表示层，与具体值无关（`0.1` 必然失真），因此固定报 D，不做数据验证。

## 6. 现状缺口汇总

| 缺口 | 严重度 | 证据 |
|---|---|---|
| `MODIFY` / `RENAME` / `COMMENT` / `REORDER` 不校验目标列存在 | **高**（生成非法 SQL） | `StructureModelBuilder.cs:169-198` 静默新增列 |
| 表不存在时 `ADD` 隐式建表、FK/CHECK 隐式建表 | **高**（污染后续链） | 同上 172–180、98–106、121–129 |
| `CREATE TABLE` 中的列不进入"已存在"集合，导致重复 ADD 漏报 | 高 | `ChangeChecker.cs:70-81` 只比对 ADD 操作 |
| 索引 / 约束 / 外键 / 序列的 DROP 目标存在性 | 中 | 模型不跟踪索引与序列 |
| 数据节点的表/列存在性与 `where` 义务 | 高 | 无检查 |
| `stage="before"` 的引用边界 | 中 | 文档已写，未实现 |
| 后果层（收窄 / 缺 where / 重命名提示） | 高 | 见 §5 |
| 以上全部均无对应测试 | — | `Check` 的测试集中在 E101–E110 |

---

# 第二部分：校验方案

## 7. 流水线

```
migrations/*.xml
  │ ① 解析（ChangeUnitParser，L1 形式合法性）
  ▼
ChangeUnit[] ── checksum 保证历史不可变（§4.6.3）
  │ ② 沿链推进：S(i) = Apply(S(i-1), U_i)
  │     ├─ 引用解析 → L2 诊断（E111–E118）
  │     ├─ 单元内时序 → L3 诊断（E105/E109/E119）
  │     └─ 一致性字典 → L4 诊断（E101–E104/E110）
  ▼
ChangeSet[]  ── ③ 新旧定义 diff
  │ ④ 后果规则引擎（L5，纯函数） → E054–E056/W034–W039
  │ ⑤ 方言能力（L6，编译期）    → E041–E053
  ▼
Finding[] ── ⑥ 传播：CLI / SQL 头 / 审计 md / --strict-warnings
```

链上下文检查（§2.2 的 C1–C3）不进入 `S(i)` 推进，作为旁路门禁并行运行：C1 校验依赖图，C2 校验发布完整性，C3 校验方言能力版本；其诊断与结构层诊断在传播阶段合并。

**硬约束**：② 必须顺序推进（`S(i) = Apply(S(i-1), U_i)`），禁止每单元从头重放。实测当前 `Commands.Schema.cs:402`、`SchemaExecutor.cs:143` 都是后者，N=10000 时 23 s，顺序推进后 < 1 s。

## 8. 数据结构

```
UnitResolution
 ├─ ChangeId
 └─ Resolutions: ReferenceResolution[]
      ├─ Kind(Table|Column|Index|Constraint|ForeignKey|Sequence)
      ├─ Table / Name
      ├─ Status(Found | NotFound | Created | Duplicated)
      └─ OperationIndex

ChangeSet
 ├─ ChangeId
 └─ TableChanges: TableChange[]
      ├─ Table / Kind(Create|Drop|Rename|Alter)
      ├─ ColumnChanges: ColumnChange[]
      │    ├─ Name / Kind(Add|Drop|Rename|Modify)
      │    ├─ Before / After : ColumnDefinition?
      │    └─ Delta: TypeShrunk | LengthShrunk | PrecisionShrunk |
      │              NullabilityTightened | AutoIncrementChanged |
      │              DefaultChanged | CollationChanged | FamilyChanged
      ├─ PrimaryKeyChange / IndexChanges / ForeignKeyChanges / CheckChanges
      └─ DataOps（set/where 是否为空）

Finding
 ├─ RuleId / Layer(L1–L6) / Category(D|V|B|S)
 ├─ Severity(E|W|I) / ChangeId / Table / Column
 ├─ Message（为什么非法/危险 + 怎么写才对）
 └─ ProbeSql（V 类）
```

`Finding` 复用现有 `CompileDiagnostic`（新增可选 `Layer`、`Category`、`ProbeSql`），不新开诊断通道。

## 9. 策略与门禁

| 配置项 | 默认 | 作用 |
|---|---|---|
| `strictReferenceCheck` | **true** | L2 存在性，建议默认开；表由既有系统创建（链里没有 `CREATE`）或零配置模式时设为 false，否则会报 E111 |
| `destructiveSeverity` | `error` | 可降为 warning（测试环境） |
| `compatBreakingSeverity` | `warning` | B 类，CI 建议 error |
| `dataDependentSeverity` | `warning` | V 类 |
| `verifyDataBeforeTighten` | false | v1.1：apply 前真实执行探针 |
| `--strict-warnings` | 关 | 把 W 升级为失败 |

注意：**L2 不受破坏性操作影响**。允许破坏性操作 ≠ 允许生成非法 SQL。这是本次概念修正后最重要的一条边界。

## 10. 性能与前置约束

实测（Windows，每单元 3 条语句，20 张表轮转）：

| 环节 | N=1,000 | N=10,000 |
|---|---|---|
| 解析 XML | 0.3 s | 3 s |
| 状态推进（当前 O(N²) 重放） | 0.2 s | 23 s |
| 状态推进（顺序推进后） | — | < 1 s |
| 状态文件写入（apply） | 111–133 s | 未测（线性外推 ~20 min） |

L2/L3/L4 都是内存字典查找（O(1)/项），增量成本可忽略；L5 需要保留上一状态做 diff，只需同时持有 `S(i-1)` 与 `S(i)` 两份模型。

**硬约束**：不得引入"每次检查写结果文件"。要缓存必须先完成 `VersionStore` 的 append-only 改造（NDJSON/WAL + 定期 compact）。

## 11. 分期

| 版本 | 范围 |
|---|---|
| **v1.0** | L2 全部（E111–E118，含取消隐式建表）；L3 的 E119；L5 的 E054–E056、W034–W039；`--strict-warnings` 接线 |
| v1.1 | V 类探针 SQL 生成与 `verifyDataBeforeTighten`；drift 复核合并为一次 introspect |
| v2.0 | 运维维度（锁时长、在线 DDL、并发索引），依赖方言版本 |

## 12. 验收标准

| 项 | 标准 |
|---|---|
| L2 覆盖 | §3.3 矩阵每一行至少 1 条反例测试（构造非法链，断言报对应码） |
| 静默行为清零 | 全仓不再出现"表/列不存在时静默 return 或隐式建表" |
| 误报 | 安全操作零诊断：加列（可空/带默认）、加索引、加注释、纯放宽类型、删除索引/外键 |
| 口径一致 | `check` / `compile` / `apply` / `EmbeddedApi.Check()` 四入口一致 |
| 链上下文 | C1–C3 的 E/W 诊断码均有对应测试，且 `depends` 图自洽与发布完整性分别验证 |
| 性能 | N=10000 全链 `check` ≤ 1.5 s |
| 文档 | 主方案 §4.2 补 L3 边界，§4.4 改为六层合法性 + 四维后果 |

---

## 13. 跨方言版本升级：产物可复现

> 本节解决"数据库版本变了，历史变更的产物不可复现"；"发布版本漏提交导致迭代丢失"是另一件事，见 §14。

### 13.1 问题本质

变更单元承载的是**逻辑意图**，落地物是**依赖方言版本的 SQL**。同一份 XML 在不同版本下产物不同：

| 单元 | MySQL 5.7 | MySQL 8.0 |
|---|---|---|
| `column action="RENAME"` | `CHANGE COLUMN old new <完整定义>` | `RENAME COLUMN old TO new` |
| 加 `CHECK` 约束 | 语法接受但**不生效**（W023） | 真正生效 |
| 缺当前方言 `<sql>` | 跳过（W032） | 仍然跳过 |

实证：`MySqlDialect.RenameColumn` 按 `VersionAtLeast(8, 0)`（MariaDB 为 10.5）分支。

```146:158:Src/DbChronicle/Compilation/Dialects/MySqlDialect.cs
        public override string RenameColumn(ColumnOperation operation)
        {
            if ((base.Name == "mariadb") ? VersionAtLeast(10, 5) : VersionAtLeast(8, 0))
            {
                return "ALTER TABLE " + Quote(operation.Table) + " RENAME COLUMN " + Quote(operation.RenamedFrom) + " TO " + Quote(operation.Name) + ";";
            }
            ...
        }
```

**结论：执行时的方言版本是变更产物的组成部分，不是环境噪声。** 只记"文件没变（checksum）"不足以证明"产物没变"。

### 13.2 会丢什么（四类）

| 类型 | 场景 | 后果 |
|---|---|---|
| **降级项丢失** | 旧版本下意图被弱化（W001/W002/W003/W010/W023/W024/W027/W028/W030/W031/W032/W033），升级后无人回头补 | 约束/索引/注释永久缺失，但没人知道 |
| **可复现性丢失** | SQL 快照按 `{id}.{dialect}.sql` 命名，不含版本；换版本后重新生成会**覆盖** | 无法回答"当年到底执行了什么 SQL" |
| **一致性丢失** | 状态文件只记 `dialect` 不记版本；跨版本复用同一状态目录时，已应用单元在新语义下是否仍成立无从判断 | 5.7 上跑过的链，8.0 上"看起来已完成" |
| **文件丢失** | 已应用单元文件被删/改 | 已有：`apply` 退出码 3 + `verify-checksum` |

### 13.3 当前实现的三处记录缺口（已核实）

| 记录 | 现状 | 问题 |
|---|---|---|
| 状态文件 `<applied>` | 有 `changeId/release/checksum/dialect/appliedBy/...`，**无版本** | 5.7 与 8.0 的记录无法区分 |
| SQL 快照 | `sql/{release}/{id}.{dialect}.sql` | 换版本重生成即覆盖，diff 基线丢失 |
| 降级诊断 | 只写进审计 md 的"警告"一行 | 不可机读、无法汇总成待补清单 |
| 审计 md | `database` = `dialect + " " + version` | 唯一带版本的地方，但埋在 markdown 里 |

```20:32:Src/DbChronicle/Migrations/State/VersionLogWriter.cs
        public string WriteSqlSnapshot(ChangeUnit unit, CompileResult result, string dialectName)
        {
            ...
            var path = Path.Combine(directory, unit.Id + "." + dialectName + ".sql");
            File.WriteAllText(path, result.Sql, new UTF8Encoding(false));
            return path;
        }
```

### 13.4 四个机制

#### M1 版本入账（记录层）

- `<applied>` 增加两个属性：`dialectVersion`（原始字符串）、`capabilityLevel`（见 §13.5）；缺失视为 `unknown`（向后兼容，旧状态文件可继续读）
- SQL 快照路径改为 `sql/{release}/{dialect}-{capability}/{id}.sql`，**历史快照不覆盖**
- 审计 md 保持现状（已含版本）

#### M2 执行前版本断言（执行层）

`apply` 启动时比对本环境全部已应用记录的 `capabilityLevel` 与当前配置：

| 策略 | 配置值 | 行为 |
|---|---|---|
| 严格（默认） | `versionDriftPolicy: strict` | 不一致即中止（E120），要求显式 `--accept-version-change` 或改用独立状态目录 |
| 告警 | `warn` | 输出 W 并继续 |
| 关闭 | `off` | 不检查（仅用于一次性修复场景） |

这条直接封死"在 5.7 上跑过的链，在 8.0 上无感继续"的路径。

#### M3 版本升级审计（检查层，核心）

新命令 `audit-upgrade --from <v1> --to <v2>`：

```
① 用 v2 重新编译全部已应用单元（顺序推进，O(N)；N=10000 约 3–5 s）
② 与 sql/{release}/{dialect}-{v1}/{id}.sql 逐条 diff
③ 输出三类清单
```

| 清单 | 含义 | 示例 |
|---|---|---|
| **产物变化** | 同一单元在 v1/v2 生成的语句不同 | 5.7 `CHANGE COLUMN` → 8.0 `RENAME COLUMN` |
| **降级转正** | v1 有降级、v2 不再降级 | 5.7 的 CHECK（W023）在 8.0 应重新添加 |
| **降级残留** | v1 降级、v2 仍降级 | 缺方言 `<sql>`（W032），换版本也不会自动补齐 |

输出为"待补变更清单"（表/对象/建议操作/原因），可生成补丁单元**骨架**，但**不自动执行**。

#### M4 降级台账（持久化）

每次 `apply` 把该单元的"意图丢失型"警告写入状态文件：

```xml
<applied changeId="..." dialectVersion="5.7.32" capabilityLevel="mysql57">
  <degradations>
    <degradation code="W023" table="nor_user" column="" object="ck_age" reason="MySQL 5.7 CHECK 不生效" />
  </degradations>
</applied>
```

M3 用台账 + 重编译结果交叉验证：即使快照丢失，也能知道"当年哪些意图没落地"。

### 13.5 能力级别（capability level）

不能按精确版本比对，否则 `5.7.31 → 5.7.32` 也会触发告警。按**方言内影响 SQL 生成的阈值**定义级别：

| 方言 | 级别划分依据 |
|---|---|
| MySQL | 5.7（无 CHECK、`CHANGE` 改名）/ 8.0（原生 `RENAME COLUMN`、CHECK 生效）/ 8.4 |
| MariaDB | 10.2 / 10.5（原生 `RENAME COLUMN`） |
| SQLite | 3.24（`ON CONFLICT`）/ 3.25（原生 `RENAME COLUMN`）/ 3.35（原生 `DROP COLUMN`） |
| PostgreSQL | 按实际实现分支，无分支则单一级别 |
| SQL Server | 2016 / 2019 / 2022（按实际实现分支） |

级别由版本字符串解析得出（如 `8.0.36` → `mysql80`），状态中同时保留原始字符串便于追溯。

### 13.6 刻意不做

| 不做 | 理由 |
|---|---|
| 自动补做降级项 | 自动加 CHECK / 索引可能因脏数据失败，必须人工确认 |
| 重放已应用单元 | 违反幂等边界（§6.3），会重复执行 DDL |
| 跨产品共享同一状态目录 | `mysql ↔ mariadb` 虽被 `IsDialectCompatible` 允许，但版本阈值不同，必须走 M3 审计 |
| 按精确版本号判等 | 补丁版本升级不应触发全链告警，用能力级别 |

### 13.7 诊断码

| 码 | 触发 | 默认 |
|---|---|---|
| E120 | 已应用单元的 `capabilityLevel` 与当前配置不一致 | Error（`versionDriftPolicy: strict`） |
| W043 | 升级审计：同一单元在新级别下产物变化 | 审计报告条目 |
| W044 | 升级审计：历史降级项仍未落实（待补） | 审计报告条目 |
| W045 | 状态文件缺 `dialectVersion`（旧版本写入），无法参与版本审计 | Warning |

### 13.8 验收

| 项 | 标准 |
|---|---|
| 向后兼容 | 旧状态文件（无版本字段）可正常读取，缺版本记 W045 而非报错 |
| 断言 | 5.7 状态 + 8.0 配置 → `apply` 中止并给出 `--accept-version-change` 提示 |
| 审计 | 构造"5.7 加 CHECK + 升 8.0"场景，输出"降级转正"清单 |
| 快照 | 两个版本的快照并存，互不覆盖 |
| 性能 | `audit-upgrade` 在 N=10000 时 ≤ 10 s（解析 3 s + 编译 + 写报告） |

---

## 14. 跨发布版本：避免"迭代丢失"（漏提交 / 迟交）

> 与 §13 的区别：§13 说的是**数据库版本变了**（5.7 → 8.0）；本节说的是**发布版本过去了，变更集才补进来**（20261123 → 20261126）。后者与数据库版本无关，纯粹是发布节奏与仓库提交不同步。

### 14.1 场景

```
2026-11-23  发布 20261123 版本（打包、上线）
            某个变更集文件漏提交（漏 add / 分支未合并 / 打包脚本漏收）
            → 该版本在数据库层面缺这一块，但没人知道

2026-11-26  开发 20261126 时才提交该文件
            文件仍写 release="20261123"、id="20261126_101010"
            → 20261123 这一迭代"名义存在、实际从未按时落地"
```

### 14.2 为什么现在拦不住

**① 只有一条不分方向的警告。** 现有校验比较 `release` 与 `id` 日期，不一致就报 W012，不区分是"提前写好归入未来版本"（正常）还是"事后补进已过去的版本"（异常）；而且 W012 只是 Warning，不加 `--strict-warnings` 就不会阻断。

```853:866:Src/DbChronicle/Parsing/ChangeUnitParser.cs
            if (!string.IsNullOrEmpty(unit.Id) && unit.Id.Length >= 8)
            {
                var idDate = unit.Id.Substring(0, 8);
                if (IsDigits(idDate) && idDate != unit.Release)
                {
                    unit.Diagnostics.Add(new CompileDiagnostic
                    {
                        Code = DiagnosticCodes.ReleaseDateMismatch,
                        Severity = DiagnosticSeverity.Warning,
                        ChangeId = unit.Id,
                        Message = "release(" + unit.Release + ") 与 change_id 日期(" + idDate + ") 不一致"
                    });
                }
            }
```

**② 系统不知道"某个 release 已经发布过"。** 没有封版记录，所以无法判断"这个 release 底下新出现的单元是不是发布后才加的"。

**③ 排序会把迟到单元塞进旧版本段。** 排序键是 `(release, id)`，所以 `release=20261123` 的迟到单元会被插到 20261123 段尾；但它是按 20261126 的代码状态写的，很可能引用了 20261126 才创建的表/列 → 执行期"表不存在"，或反过来与后续单元形成顺序冲突。

```85:91:Src/DbChronicle/Parsing/ChangeUnitLoader.cs
        public static IReadOnlyList<ChangeUnit> Sort(IEnumerable<ChangeUnit> units)
        {
            return units
                .OrderBy(x => x.Release ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
```

### 14.3 三条实际危害

| 危害 | 说明 |
|---|---|
| **版本内结构不完整** | 20261123 上线后应用代码依赖的列/表实际不存在，运行期报错；且不同环境可能表现不同（谁漏了谁没漏） |
| **补交时数据已积累** | 加 NOT NULL / 唯一 / 外键在补做时可能因脏数据失败；按时发布时数据量小、冲突少 |
| **顺序倒错** | 迟到单元按旧 release 排序，却按新版本的代码状态编写，可能与后续单元互为前提 |

### 14.4 三层防线

#### L1 静态规则（零成本，立刻可做）

| 条件 | 结论 | 码 |
|---|---|---|
| `id` 日期 **>** `release` 日期，且非补丁文件 | **迟交**：写入了已过去的版本 | **E121** |
| 同上，但文件名为 `*_fix_*` / `*_patch_*` 或声明 `lateSubmission="true"` | 允许（补丁语义），提示确认 | **W046** |
| `id` 日期 **<** `release` 日期 | 预写归入未来版本，正常 | 保持 W012 提示 |

补充约束：补丁形式的迟交必须 `depends` 指向同 release 已有单元（E108 已有此规则），保证它排在正确位置。

#### L2 封版清单（发布门禁）

发布时生成不可变清单：

```xml
<!-- releases/20261123.release.xml -->
<release name="20261123" sealedAt="2026-11-23 18:00:00" sealedBy="ci" dirHash="sha256:...">
  <unit id="20261123_101010" checksum="a1b2..." />
  <unit id="20261123_142233" checksum="c3d4..." />
</release>
```

| 环节 | 动作 |
|---|---|
| 发布流水线 | `db-chronicle release --freeze 20261123` 生成清单，**打包必须基于清单**收文件 |
| 日常 `check` / `apply` | 发现已封版 release 下出现清单外单元、checksum 变化、清单内文件缺失 → **E122** |
| 清单本身 | 不可修改；要补必须先有新 release |

这一步直接覆盖"发布时漏提交、之后才补进来"——清单是发布当时拍的快照，事后补的文件天然不在其中，一进门就被抓。

#### L3 运行期版本空洞检测

`apply` / `status` 计算：每个已封版 release 的清单单元，在本环境状态文件里是否都已应用。

```
版本空洞：20261123 已发布 5 个单元，本环境应用 4 个
  缺失：20261123_142233（ALTER nor_smpinfo ADD COLUMN ...）
```

报 **E123**。这直接回答"我这个环境是不是漏了某个迭代的变更"。

#### L4 环境间比对（可选）

`status --compare envA envB`：比较两个环境已应用的 changeId 集合与 checksum，找出"某环境缺某 release 的单元"。适合多环境部署的收尾核对。

### 14.5 正确补救姿势

| 做法 | 结论 |
|---|---|
| 改为**新 release**（如 20261126）+ `depends` 指向原 release 最后一个单元 | **推荐**：时间线诚实，顺序正确 |
| 保留原 release，显式 `lateSubmission="true"` + `depends` | 允许，但 `apply` 需输出醒目提示并要求 `--accept-late-submission` |
| 静默按旧 release 提交（就当前实现） | **禁止**（E121） |
| 修改已封版清单来"让它通过" | **禁止**（清单不可变，改了等于销毁证据） |

### 14.6 诊断码

| 码 | 触发 | 默认 |
|---|---|---|
| E121 | 迟交：`id` 日期晚于 `release`，且非补丁/无声明 | Error |
| E122 | 已封版 release 下出现清单外单元、checksum 变化或文件缺失 | Error |
| E123 | 运行期版本空洞：已封版 release 的单元未在本环境应用 | Error（`status` 报告） |
| W046 | 补丁式迟交（`_fix_` / `_patch_` / `lateSubmission`） | Warning |

### 14.7 验收

| 项 | 标准 |
|---|---|
| 静态 | 构造 `release=20261123` + `id=20261126_x` 的普通单元 → E121；加 `_patch_` 前缀 → W046 |
| 封版 | 封版后新增同 release 单元 → E122；修改已封版单元内容 → E122 |
| 空洞 | 从清单中删一条后 `status` → E123 并能指出具体 changeId |
| 顺序 | 迟交单元若引用了更新 release 才建的表 → L2/L3 之外还应由引用存在性（E111）兜住 |
| 兼容 | 无 `releases/` 目录的历史项目：跳过 L2/L3，仅 L1 生效，且不报错 |

### 14.8 方案评估：单元级 `depends` 链够不够

常见做法是"每个变更单元写上一条（上一个 changeId / 上个版本号），用链保证连续性"。这个方案的定位需要说清楚。

#### 能解决的

| 问题 | 机制 |
|---|---|
| 引用了不存在的单元 | E107（已实现） |
| 顺序与依赖不一致 | 链序 vs 文件名序，可校验 |
| 单元被删除 / 改名后悬空 | 子节点 depends 找不到父节点 |

#### 抓不到本场景的原因

推演一次（`X` 是 20261123 漏提交的那个单元）：

```
仓库（已提交）：… → 20261123_A          父节点 = 20261120_Z
本地未提交：      X（depends = 20261123_A）
20261126 开发：   C（depends = 20261123_A）
之后 X 补提交：   X（depends = 20261123_A）
```

最终图是 `20261123_A → {X, C}`，两个子节点。从工具视角看：

- 所有 `depends` 都存在 → E107 不报
- 没有断链 → 链完整性检查通过
- 排序合法 → 无告警

**零诊断**。而真实问题是"20261123 发布时库里没有 X"。

> 根因：链只能检测"**引用了不存在的节点**"，不能检测"**某个节点从未进入链**"。漏提交的本质就是"它不在链上"，而链检查看不到链外的东西。

#### 另外五个结构性问题

| 问题 | 说明 |
|---|---|
| **并行开发天然分叉** | 多人各自基于同一父节点编写，合并后必为 DAG；强制单链等于强制串行，团队规模一大就写不下去 |
| **开发者看不到最新链头** | 本地未拉取时容易写错父节点；若写成"存在的旧节点"，静默形成错误分支，比断链更难发现 |
| **粒度错配** | 写"上个**版本号**"只保证 release 之间的顺序，挡不住 release **内部**漏提交；写"上个**单元**"则退化为单链，撞上并行问题 |
| **空版本** | 纯代码版本没有任何单元，链上无节点，必须允许跳过，否则链断 |
| **删除即断链** | 未发布的单元被放弃 / revert，所有子节点悬空，需要人工修链 |

#### 判定

`depends` 链是**必要条件，不是充分条件**：

- 它保证"**已声明的那张图是自洽的**"；
- 它无法保证"**这张图是完整的**"——完整性需要外部真值（发布时拍的快照），不能由被检查对象自己声明。

因此 §14.4 的三层防线仍然必要，`depends` 只是其中的"图自洽"部分。

#### 推荐的组合（按成本递增）

| 方案 | 成本 | 能防住 |
|---|---|---|
| ① `depends` 声明（现有） | 极低 | 断链、悬空引用、顺序错乱 |
| ② 迟交静态检测（E121） | 极低 | `id` 日期明显晚于 `release` 的情况 |
| ③ **链头锚定**（轻量替代封版清单） | 低 | 下个 release 的第一个单元必须 `depends` 指向"发布时记录的链头"，否则 E124 |
| ④ 封版清单（§14.4-L2） | 中（要改发布流程） | **唯一能证明"完整性"的手段** |
| ⑤ 分叉信号（辅助，默认关） | 低 | 同一 release 内出现来自不同父节点的多链头时提示 W047；并行开发下误报率高，只建议在评审环境开 |

③ 是 ④ 的轻量版：不存全量单元清单，只在发布时记一个 `chainHead`。它比 ① 强，因为锚点是"发布那一刻的事实"，而不是"开发者记得的父节点"；但若发布时漏了多个单元、且这些单元都被写成链头之后，仍然要靠 ④ 兜底。

#### 结论

原方案（写上个版本号）**不算错，但定位要降级**：它是**顺序与图自洽**的保障，不是**发布完整性**的保障。真正能回答"20261123 这个迭代有没有漏"的，只有 ④ 封版清单；次优是 ③ 链头锚定。三者叠加才闭环。

这一评估属于 §2.2 的第二轴：C1 只覆盖图自洽，C2 才覆盖发布完整性；它不属于 L1–L6 结构合法性轴。

#### 补充诊断码

| 码 | 触发 | 默认 |
|---|---|---|
| E124 | 链头锚定失败：某 release 的第一个单元未 `depends` 指向发布时记录的 `chainHead` | Error（仅启用 ③ 时） |
| W047 | 同一 release 内出现多个链头（疑似并行分叉或迟交） | 默认关 |

---

### 14.7 读取顺序与排序

`ChangeUnitLoader.LoadAll()` 先按文件名读取，再统一 `Sort(units)`：`OrderBy(release).ThenBy(id)`（均 OrdinalIgnoreCase）。因此**文件在磁盘上的读取顺序不影响执行顺序**；`apply` 在 `ApplyWith` 内还会再排一次，`compile` / `check` / 内嵌 API 走的也是排好序的列表。`release` 由解析器校验为 `yyyyMMdd`（E006），所以字符串比较等同时间比较；基线 `00000000` 自然排在最前。

排序解决不了的是**运行期已经越过某个版本**的场景：例如状态文件里已应用 20261126，之后仓库里才补进一个 20261124 的单元。排序会把它放在 20261126 之前，但 20261126 已应用会被跳过，实际执行顺序变成"先 20261126、后 20261124"。这是 E127：待应用单元的 `(release, id)` 低于已应用最高版本 → Error，`apply` **直接抛 `ChainValidationException`（携带 Diagnostics）拒绝执行**，20261124 不会被执行；`--accept-late-submission` / `options.allowLateSubmission=true` 时降为 W046（显式接受迟交）。

`release` 写成 7 位（如 `2026115`）不会被"排序错"，而是解析阶段直接 E006（格式必须 `yyyyMMdd`）。

## 15. 基线种子：把"已确认的结构"固化为新起点
### 15.1 想法与价值

```
每次（或每个 release）升级成功后，把库的真实结构导出为一份种子基线
后续升级 = 该种子 + 之后的增量单元
```

| 价值 | 说明 |
|---|---|
| **性能** | 全链回放 O(N²) → 只回放「种子 + 少量增量」，N 恒定（建议 ≤ 50） |
| **接入成本** | 新环境 / 新人不需要跑完整历史链 |
| **发布事实** | 种子是"这个 release 在库里到底长什么样"的物证，比"以为的结构"可靠 |
| **顺带防漏** | 种子（真实结构）与全链回放（模型）对不上 → 暴露漏执行 / 漏提交 |

### 15.2 四条硬前提（缺一条就会把错误永久化）

| # | 前提 | 违反后果 |
|---|---|---|
| 1 | 种子必须从**真实库 introspect** 得到，**不能从累积模型导出** | 把"以为的结构"固化成"事实"，所有环境随后基于错误基线 |
| 2 | 生成前必须做 **drift 校验**（模型 vs 库，`SchemaIntrospector.Compare`） | 手工改动、半态残留随种子被合法化 |
| 3 | 种子必须带 **release + 方言/能力级别 + 内容哈希**，并与其他环境比对 | 同一 release 各环境种子不同却都算"正确" |
| 4 | **不得清空审计证据与状态记录**（版本空洞检测、降级台账、已应用集合） | §13/§14 全部失效 |

第 1 条是本节最重要的结论：**种子是观测结果，不是推算结果**。

### 15.3 生成时机：每个 release 完整落地后输出一份

种子的粒度是 **release**，不是"每次 `apply` 调用"、也不是"每条 DDL"。

| 方案 | 问题 |
|---|---|
| 每次 `apply` 调用成功 | 同一 release 内多次续跑会生成多份中间态种子；未验证的结构被固化成基线；多环境哈希必然不一致 |
| **每个 release 的全部单元应用成功后**（推荐） | 与发布事实对齐，一 release 一份，可与封版清单互为校验 |
| 每 N 个 release / 每季度 | 数量少，但增量段变长，性能收益下降 |

**触发条件（必须全部满足，缺一不生成）**：

1. 该 release 下的单元**全部**处于已应用状态（无失败、无跳过、无半态）；
2. drift 校验为空（模型 == 库）；
3. 该 release 的封版清单存在且 checksum 全部一致（§14.4-L2 已启用时）；
4. 生成后写入 `seeds/{release}.seed.xml` + `.meta.xml`，并打上 `baselineRelease`。

即：**种子的版本号 = 它代表的 release**，一一对应，一个 release 不允许有两份不同内容的种子（重复生成必须内容哈希一致，否则报错）。

### 15.4 双轨制：检查在全链，执行用种子

| 场景 | 用哪一轨 | 理由 |
|---|---|---|
| CI `check` / 破坏性校验 | **全链** | 破坏性检查需要 `S(i-1)`；降级台账、版本空洞都依赖全链 |
| 开发环境搭库 | 全链 或 种子 | 两者皆可 |
| 生产 / 新环境部署 | **种子 + 增量** | 性能与接入成本 |
| 升级审计（§13） | **全链 + 快照** | 需重编译历史单元 |

即：**种子是执行加速器，不是检查豁免证**。若为省时间让 CI 也只跑"种子 + 增量"，等于放弃 §5/§14 的全部能力。

### 15.5 与"迭代丢失"的关系

正向：若 20261123 漏提交单元 X，发布 20261126 时生成种子，则「种子（真实结构）≠ 全链回放（含 X 的模型）」，drift 校验会报差异 → 间接暴露漏执行。

局限（不能替代封版清单）：

- 只能发现**模型能表达**的差异。`SchemaIntrospector.Compare` 当前只比列，索引 / 外键 / CHECK 的缺失查不出来；
- 只能在**生成种子那一刻**发现，回答不了"历史上的 20261123 是否完整"。

所以 §14 的封版清单 + 版本空洞检测仍是必需的。

### 15.6 状态与回放起点

| 数据 | 处理 |
|---|---|
| 已应用单元记录 | **保留**，可压缩为「release → 单元数 + 摘要哈希」归档段，不再逐条参与回放 |
| 降级台账 | **保留全文**（意图缺失的唯一证据） |
| 单元文件 | **保留**（不可变规范），只后移回放起点 |
| 回放起点 | 状态新增 `baselineRelease`；`ChangeUnitLoader` 只加载 `release > baselineRelease` |
| 种子文件 | `seeds/{release}.seed.xml` + `{release}.seed.meta.xml`（哈希、方言、能力级别、生成时间、行数） |

### 15.7 风险清单

| 风险 | 缓解 |
|---|---|
| 种子固化了 drift | 生成前 drift 必须为零差异，否则拒绝（E125） |
| 种子被手工编辑 | 内容哈希校验 |
| 不同环境种子不一致 | release 级哈希比对（W048） |
| 压缩后无法追溯更早变更 | 原全链文件保留不删，只改回放起点 |
| 压缩后无法回滚到种子之前 | 明确声明：种子是"不可回退点" |
| 种子泄露数据 | meta 只存行数，不存内容 |

### 15.8 落地形态

```
db-chronicle seed create --release 20261123 --from <conn> [--baseline-release 20261123]
db-chronicle seed verify --release 20261123 --against <conn>   # drift + 哈希
db-chronicle seed list
```

`seed create`：introspect → 与全链模型 diff（必须为空）→ 写种子 + meta → 记录 `baselineRelease`。

可直接复用的既有能力：`Scanner.Database`（从库反向生成 XML）、`BaselineXmlWriter`（基线与扫描共用）、`SchemaIntrospector.Compare`（drift）。

### 15.9 诊断码与验收

| 码 | 触发 | 默认 |
|---|---|---|
| E125 | 生成种子前 drift 校验非空，拒绝固化 | Error |
| W048 | 同 release 的种子哈希与其他环境不一致 | Warning |

| 验收项 | 标准 |
|---|---|
| 一致性 | 同一 release 在两个环境生成的种子哈希一致 |
| 防错 | 手工改库后生成种子 → E125 拒绝 |
| 回放 | 设 `baselineRelease` 后只回放之后的单元，诊断结果与全链一致（对相同增量） |
| 证据 | 压缩状态后 `status` 仍能报版本空洞；降级台账仍可查 |
| 回滚 | 回滚目标早于种子时明确报错，不静默降级 |

### 15.10 边界：仅结构种子

种子的定位已定：**只承载结构，不含业务数据快照**。

| 形态 | 内容 | 入库 | 说明 |
|---|---|---|---|
| **结构种子** | 逻辑模型 XML（表/列/索引/约束/外键） | 是 | 与 `migrations/` 同级，可 diff、可评审 |
| 业务数据 | 行数据 | **否** | 不进种子；需要时另用备份还原 |
| 字典 / 配置数据 | `<seed>` 节点 | 单独处理 | 见下 |

#### "仅结构"会带出一个必须处理的洞

`<seed>` 是**挂在迁移单元上的数据插入**。一旦设置 `baselineRelease = 20261123`，该 release 及之前的单元不再回放 → **它们的 `<seed>` 也不会执行** → 新环境结构齐全、字典表为空，应用起不来。

| 方案 | 做法 | 评价 |
|---|---|---|
| **A 累积数据种子**（推荐） | 生成 `seeds/{release}.data.xml`：把 `release ≤ V` 的所有 `<seed>` 行合并成一份随结构种子配对导入 | 只含字典/配置，体积可控（通常几百行） |
| B 保留数据单元 | 数据类单元（`<seed>` / `<update>`）不参与压缩，回放起点后仍按序执行 | 结构压缩、数据不压缩；需要"只执行数据部分"模式，复杂度高 |
| C 不处理 | 新环境由部署脚本手工灌字典 | 易漏、各环境不一致 |

方案 A 的产物仍是"配置类数据"，不是业务数据，与"仅结构种子"不矛盾，但必须显式纳入设计——否则每次压缩都会静默丢字典。

#### 额外收益：种子天然是"版本间净结构差异"

`seeds/20261123.schema.xml` 与 `seeds/20261126.schema.xml` 的 diff 就是该版本的全部结构变更，可直接作为发布评审材料，比逐个读 changeUnit 直观。

#### 落地成本最低

结构种子复用 `Scanner.Database` + `BaselineXmlWriter`（`scan-db` 已能产出同格式 XML），不需要另写序列化逻辑。

### 15.11 导入种子后的状态记录（与 §14 的冲突点）

从种子建库的环境，"已应用"不等于"执行过 DDL"。若把种子覆盖的单元直接写成 `<applied>`，会连锁出三个问题：

| 问题 | 后果 |
|---|---|
| §14-L3 版本空洞检测 | 无法区分"种子覆盖"与"真的执行过"，判定失真 |
| §13 产物审计 | 没有当时的 SQL 快照，重编译对比失去基线 |
| 审计日志 | 该环境缺失历史执行记录 |

正确做法：**新增种子覆盖段，不伪造 `applied` 记录**：

```xml
<state>
  <seedCoverage release="20261123" seedHash="sha256:..." importedAt="..." importedBy="..." />
  <applied changeId="20261126_101010" ... />
</state>
```

语义约定：

| 规则 | 说明 |
|---|---|
| `release ≤ seedCoverage.release` | 视为"已由种子覆盖"，L3 空洞检测**跳过该区间**，不逐条校验 |
| `release > seedCoverage.release` | 正常逐条记录与校验 |
| `status` 展示 | "本环境结构基线 = 20261123（种子），其后应用 N 个单元" |
| 单向性 | 导入后不可修改 `seedCoverage`；要换基线只能重建环境 |
| 唯一性 | 一个状态文件只允许一个 `seedCoverage`（不支持嵌套基线） |

#### 导入流程

```
新环境 / 重构环境：
  ① 选种子版本 V（= 部署目标版本，或目标之前最近的种子）
  ② 校验种子文件哈希 == meta 哈希（E126）
  ③ 导入结构 + 数据种子
  ④ 写 seedCoverage(V)
  ⑤ 依次 apply release > V 的增量
```

这样"每次更新获取种子库后依次迭代"就闭环了：**种子给起点，增量给迭代**。

注意一个边界：若种子版本 **高于** 目标部署版本（例如拿了 20261126 的种子去部署 20261123），属于倒退，必须拒绝（W049）。

### 15.12 与检查的关系（重申）

- 种子里只有"结果"，没有"过程" → 破坏性检查（§5）、降级台账（§14）、升级审计（§13）仍须在全链上做；
- 种子参与检查的唯一方式是**与全链模型做 drift 比对**（能发现"模型有、库里没有"，即漏执行）。

---

## 16. 实现状态（2026-11-27）

端到端流程、异常契约与配置项见 `Docs/DbChronicle 实现总览.md`；本节只记录检查项的实现状态。

| 范围 | 状态 | 落点 |
|---|---|---|
| L2 E111–E119 | 已实现（含通配符 `table="*"` 跳过、`strictReferenceCheck`、同单元 `CREATE` 列进入已知集合） | `Compilation/Checks/ChangeChecker.cs` |
| L2 去隐式建表 | 检查器内推进状态后按“未在本单元显式创建”剪枝，后续单元不再被虚假表/列污染；`StructureModelBuilder` 保持不变（编译路径仍容错，非法链由检查器拦截） | `ChangeChecker.PruneImplicitState` |
| L3 E119 | 已实现 | `CheckStageBeforeBoundary` |
| L5 E054–E058 / W034–W040 | 已实现（缺省 `where`、类型/长度/精度收窄、字符集收窄、主键/自增变更、约束收紧、Text→String、重命名、类型族、字符集/排序规则、破坏性 rawSql、Decimal 缺省精度） | `Compilation/Checks/ConsequenceChecker.cs` |
| 四入口一致 | `check` / `compile` / `apply` / `EmbeddedApi.Check()` 均执行链校验；`apply` 链校验失败返回 `ExitCode=1` 并带 `Diagnostics` | `Commands.Schema`、`SchemaExecutor.ApplyWith`、`DefaultDbChronicle` |
| 门禁开关 | `options.strictReferenceCheck`（默认 true；零配置模式默认 false） | `OptionSettings` / `ConfigLoader` / CLI `--strict-reference-check` |

| C2 发布版本链 | E121 迟交、W046 补丁迟交、E122 封版清单漂移、E123 版本空洞、E127 升级顺序倒挂、`release --freeze`、`--accept-late-submission` 已实现；E124（链头锚定）/ W047（多链头）未做 | `Compilation/Checks/ReleaseChecker.cs`、`Migrations/State/ReleaseManifestStore.cs`、`Commands.Release.cs` |
| C3 能力版本链 | `database.capabilityLevel` + `options.versionDriftPolicy`：E120（strict）、W043（warn）、W045（旧状态缺字段）已实现；W044（降级台账）、SQL 快照 diff 未做 | `Compilation/Checks/CapabilityChecker.cs`、`VersionStore` |

未实现（按分期保留）：

- W041（时间小数秒无法用结构化节点表达）、W042（PG `BYTEA` 无长度约束）——需要方言感知与更细的类型模型，留 v1.1。
- V 类探针 SQL（W034 的 `ProbeSql`）与 `verifyDataBeforeTighten`——v1.1。
- C2 的 E124（链头锚定）/ W047（同 release 多链头）——默认关闭的可选机制，未实现。
- C3 的 W044（历史降级项台账）与 SQL 快照 diff（W043 目前只比较能力级别字段，不比较产物）——未实现。
- 基线种子（E125/E126、W048/W049）——未实现。
- 顺序推进性能（§7 硬约束）：仍为每单元全链重放，N 大时是已知成本，未在本轮改动。

测试：`Src/Tests/DbChronicle.Tests/ChainCheckTests.cs` 覆盖 E111–E119、E054–E058、W034–W040、通配符、严格/宽松模式、隐式状态剪枝、数据节点与种子行数；`ReleaseChainTests.cs` 覆盖 E121/W046/E122/E123/E127、封版清单不可覆盖、读取顺序无关（按 release/id 排序）与 W043/W045/E120。

---

## 附录 A：诊断码分配

**职责归属**（§2.1）：A 类不可豁免，B 类需人工确认与备份后直接执行。

| 职责 | 码段 |
|---|---|
| **A 可执行性** | E001–E053（形式与方言）、E101–E110（一致性）、E111–E119（引用与时序）、V-01~V-05（运行时失败的离线投影） |
| **B 历史无影响** | E054–E058、W034–W042、V-06 |

| 码 | 层 | 含义 | 状态 |
|---|---|---|---|
| E054 | L5-D | 类型收窄 / 长度缩短 / 精度缩减（需人工确认/备份） | 新增 |
| E055 | L5-D | 删主键 / 换主键 / 改自增（需人工确认/备份） | 新增 |
| E056 | L5-D | 数据改写操作缺 `where` | 新增 |
| E057 | L5-D | **精度损失**：定点整数位/小数位缩减、浮点有效位下降、定点↔浮点互转、整数装不下 | 新增 |
| E058 | L5-D | 字符集收窄（不可表示字符会被替换或写入失败） | 新增 |
| E111 | L2 | 操作目标表在变更链中不存在（ALTER/DROP/COMMENT/主键等） | 新增 |
| E112 | L2 | 操作目标列不存在（MODIFY/RENAME 源/COMMENT/REORDER/主键列/索引列/外键列/数据节点列） | 新增 |
| E113 | L2 | ADD 的列已存在（含 `CREATE TABLE` 与基线中已定义的列） | 新增 |
| E114 | L2 | 主键操作前提不成立（ADD 时已有主键 / DROP 时无主键） | 新增 |
| E115 | L2 | 索引 / 约束 / 外键 / 序列的 DROP 或 RENAME 目标不存在 | 新增 |
| E116 | L2 | 外键引用的表或列不存在 | 新增 |
| E117 | L2 | RENAME 的源表 / 源列不存在 | 新增 |
| E118 | L2 | `seed` 列不存在或行值个数与列数不一致 | 新增 |
| E119 | L3 | `stage="before"` 引用了本单元新增的表/列 | 新增 |
| E120 | 跨版本 | 已应用单元的 `capabilityLevel` 与当前配置不一致（`versionDriftPolicy: strict`） | 新增 |
| E121 | 发布版本 | 迟交：`id` 日期晚于 `release`，且非补丁/无 `lateSubmission` 声明 | 新增 |
| E122 | 发布版本 | 已封版 release 下出现清单外单元、checksum 变化或文件缺失 | 新增 |
| E123 | 发布版本 | 版本空洞：已封版 release 的单元未在本环境应用 | 新增 |
| E127 | 发布版本 | 升级顺序倒挂：待应用单元的 `(release, id)` 低于状态文件中已应用的最高版本 | 新增 |
| E124 | 发布版本 | 链头锚定失败：某 release 首个单元未 `depends` 指向发布时记录的 `chainHead`（可选机制） | 新增 |
| E125 | 基线种子 | 生成种子前 drift 校验非空（模型与库不一致），拒绝固化 | 新增 |
| E126 | 基线种子 | 导入种子时内容哈希与 meta 不符（文件被改或版本错配） | 新增 |
| W034 | L5-V | 约束收紧需数据验证（附探针 SQL） | 新增 |
| W035 | L5-V | `Text → String(len)` 需确认最大长度 | 新增 |
| W036 | L5-B | 重命名导致外部引用断裂 | 新增 |
| W037 | L5-S | 类型族改变 | 新增 |
| W038 | L5-S | 字符集 / 排序规则变更 | 新增 |
| W039 | L5-D | rawSql 含破坏性关键字 | 新增 |
| W040 | L5-D | `Decimal` 未显式声明 precision/scale，将使用默认 `(18,2)` | 新增 |
| W041 | L5-D | 时间小数秒精度无法用结构化节点表达，建议 `<rawSql>` | 新增 |
| W042 | L6 | PG 下 `Binary`/`Blob` 映射为 `BYTEA`，`length` 不产生物理约束 | 新增 |
| W043 | 跨版本 | 升级审计：同一单元在新能力级别下 SQL 产物变化 | 新增 |
| W044 | 跨版本 | 升级审计：历史降级项仍未落实（待补清单） | 新增 |
| W045 | 跨版本 | 状态文件缺 `dialectVersion` / `capabilityLevel`（旧版本写入） | 新增 |
| W046 | 发布版本 | 补丁式迟交（`_fix_` / `_patch_` / `lateSubmission`），允许但需确认 | 新增 |
| W047 | 发布版本 | 同一 release 内出现多个链头（疑似并行分叉或迟交），默认关闭 | 新增 |
| W048 | 基线种子 | 同 release 的种子哈希与其他环境不一致 | 新增 |
| W049 | 基线种子 | 种子版本高于目标部署版本（版本倒退） | 新增 |

已占用不可复用：E001–E053（含 E052 `AutoIncrementModifyUnsupported`、E053 `ColumnReorderBeforeUnsupported`）、E101–E110、W001–W033。

## 附录 B：社区对标

| 工具 | 对应层 | 可借鉴点 |
|---|---|---|
| Flyway / Liquibase | L4 | 不可变迁移 + checksum，证明"历史无需重查语义" |
| EF Core `ModelSnapshot` | L2/L3 基准 | 累积状态物化，新迁移只与快照比 |
| Django `ProjectState` | L2/L3 基准 | 内存 replay 全链（O(N)，代价低） |
| Prisma shadow database | 全链 | 空库重放，作为正确性基准 |
| Atlas `migrate lint` | L5 | 三分类：`destructive`（丢数据）/ `data_depend`（取决于数据）/ `backward_incompatible`（引用断裂） |
| Squawk（78 条） | L5 | 规则命名与清单：`adding-not-nullable-field`、`adding-unique-constraint`、`changing-column-type`、`renaming-column` |
| `strong_migrations` | L5 | 危险迁移默认阻断 + 显式豁免 |

**结论**：L5 的规则集可直接借鉴；**L2/L3（升级链引用合法性）没有成熟对标物**——多数工具靠"必须连库才能 diff"，DbChronicle 的离线累积模型反而是做这一层的优势。

## 附录 C：待确认项

1. E111–E119 / E054–E056 / W034–W039 的编号分配是否采纳；
2. `strictReferenceCheck` 是否默认开启（本文按"默认开"设计，因为报的是非法 SQL）；
3. 表不存在时是否保留"隐式建表"以兼容外部系统建表的场景（本文建议改为就地提示 + 占位表，不再静默建表）；
4. `update`/`clean`/`backfill` 的 `where="1=1"` 是否作为显式逃生门；
5. `seed conflict="UPDATE"` 是否独立为一条诊断（暂并入 W034 说明）；
6. 精度损失是独立成 E057/E058（本文建议，便于 CI 白名单按类别豁免），还是并入 E054 用子类型字段区分；
7. `ColumnMerger` 是否要在类型族切换时**清空**不适用的旧属性（Length/Precision/Scale），还是仅由 W040 提示——本文建议清空并告警，避免生成 `VARCHAR(0)`。


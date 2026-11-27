# XML Value IntelliSense（VS 2022 扩展）

给 Visual Studio 2022 的 XML 编辑器补上 XSD 驱动的属性值补全：

- 属性值候选来自文档关联的 XSD（`xsi:noNamespaceSchemaLocation` / `xsi:schemaLocation`）
- 修改已有属性值时也会自动重新弹出（VS 原生只在首次输入时弹）
- 支持输入前缀/子序列过滤：`mo` / `mfy` 都能匹配 `MODIFY`
- 删除字符时自动收起候选；继续输入时按当前前缀重新弹出
- 回车提交当前候选（不会插入换行），提交后不会立刻再次弹出
- 仅对声明了 schema 的 XML 生效，不影响配置文件和其他 XML
- 支持枚举、布尔、`xs:union`、`xs:import/include`，候选说明来自 `xs:documentation`
- XSD 中必填（`use="required"`）或带枚举/布尔取值的属性，值为空时按 **错误** 处理：红色波浪线 + 错误列表条目（不再只是绿色建议波浪线）

## 环境要求

- Visual Studio 2022（17.x，64 位）
- XML 文件里声明 schema，例如：

```xml
<changeUnit xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xsi:noNamespaceSchemaLocation="..\..\XmlXSD\DbChronicle.ChangeUnit.xsd"
            id="20190516_000000" author="legacy" release="20190516">
```

或带命名空间：

```xml
<root xmlns="urn:example"
      xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
      xsi:schemaLocation="urn:example .\schemas\example.xsd">
```

没有声明的 XML 不会激活，避免误打扰。

## 随时增加自定义 XSD

插件不内置任何 XSD 列表，也不需要在插件里注册。任何本地 XSD 都可以随时接入：

1. 把新的 `.xsd` 放在任意本地目录；
2. 在 XML 根节点声明它：

```xml
<!-- 无命名空间 -->
<changeUnit xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xsi:noNamespaceSchemaLocation="..\schemas\My.Custom.xsd">
```

```xml
<!-- 有命名空间，可声明多对 -->
<root xmlns="urn:my"
      xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
      xsi:schemaLocation="urn:my ..\schemas\my.xsd
                          urn:other ..\schemas\other.xsd">
```

3. 回到属性值里继续输入，下一次补全就使用新 XSD。**不需要重启 VS，不需要重新编译插件。**

生效规则：

- **声明从当前编辑缓冲区读取**，改了 `xsi:*SchemaLocation` 不保存也会立即生效；
- XSD 文件内容变化按修改时间自动失效重载；
- 支持 `xs:import` / `xs:include`（本地文件）；
- 支持枚举、布尔、`xs:union`；
- 只支持本地路径（相对路径按 XML 文件目录解析，也支持绝对路径和 `file:///`）；
- `http(s)://` 的远程 XSD 默认拒绝；
- 只支持 XSD 1.0（.NET `XmlSchemaSet` 的限制），XSD 1.1 不支持；
- 没有在 XML 里声明 schema、只通过 VS “XML → 架构”对话框挂的 XSD，插件看不到；
- 未保存的新文件（无文件路径）无法解析相对路径。

## 构建

在 PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack.ps1 -Configuration Release
```

产物：

```
bin\Release\XmlValueIntelliSense.vsix
```

`pack.ps1` 会自动定位 VS 2022 的 MSBuild 和 `VsixUtil.exe`。工程里 `CreateVsixContainer=false` 是为了绕开部分机器上 NuGet 版 VsixUtil 的 side-by-side 启动问题，打包由脚本调用 VS 自带 VsixUtil 完成。

对照官方 VSIX 负载（VS 离线安装缓存里的 `payload.vsix`）发现：官方包是标准 OPC 结构——带 `_rels/.rels`、`[Content_Types].xml` 声明 rels 类型；而 VsixUtil 直接生成的 zip 会被部分版本 `VSIXInstaller` 的 GUI 解析路径拒绝（双击报“此文件不是有效的 VSIX 包 / 调用的目标发生了异常”，命令行 `/quiet` 却能安装）。因此 `pack.ps1` 在 VsixUtil 完成后按官方结构重打包：补写 `_rels/.rels`、重写 `[Content_Types].xml`（含 rels 类型），并用系统自带 `tar.exe --format=zip` 按官方顺序写出；双击与命令行安装均可正常解析。

## 安装 / 卸载

安装：

1. 关闭所有 Visual Studio 实例；
2. 推荐双击 `bin\Release\install.cmd`：它用 `vswhere` 自动定位本机任意版本的 VS 2022，并调用该实例自带的 `VSIXInstaller.exe`，不依赖固定安装路径；
3. 也可以直接双击 `bin\Release\XmlValueIntelliSense.vsix`；若本机 VS Installer 的 GUI 安装器报“调用的目标发生了异常 / 此文件不是有效的 VSIX 包”（该 GUI 解析路径在部分机器上对任何 VSIX 都可能失败，与包内容无关），改用 `install.cmd` 或上面的命令行安装，或先修复 Visual Studio Installer：

```powershell
& "<VS2022安装目录>\Common7\IDE\VSIXInstaller.exe" `
  /quiet "<路径>\XmlValueIntelliSense.vsix"
```

4. 重启 VS 2022。

分发到其他机器时，把 `XmlValueIntelliSense.vsix` 和 `install.cmd` 放在同一目录一起拷贝；目标机器只需装有 VS 2022 17.x（Community / Pro / Enterprise），不需要固定版本的 VS 安装器。

如果安装日志里先出现“已成功完成…安装”，随后出现 `ArgumentException: 已添加项。字典中的关键字: "HTTP_PROXY" 所添加的关键字: "http_proxy"`，这是安装后刷新配置时进程环境里同时存在大小写两个代理变量导致的，扩展实际上已经安装；删除其中一个环境变量（或直接用 `install.cmd` 安装）即可，不影响扩展启用。

卸载：`扩展 → 管理扩展 → 已安装`，找到 **XML Value IntelliSense** 卸载。

## 验证

打开一个带 schema 声明的 XML，例如：

```
resource\update\DataBase\20190516_000000.xml
```

1. 把光标放进 `action="MODIFY"` 的引号内，清空值，输入 `mo`；
2. 应自动弹出并显示 `MODIFY`；
3. 输入 `mfy` 也应匹配 `MODIFY`（子序列过滤）；
4. `type=""` 应出现 20 个逻辑类型；`nullable=""` 应出现 `true/false`；
5. 按 Esc 关闭后继续输入，会再次弹出；
6. 把 `action` 的值清空为 `action=""`：编辑器应出现红色波浪线，且“错误列表”中新增 1 条错误（级别为“错误”，说明包含“值不能为空”与允许值）；把值填回合法值后错误消失；
7. 打开没有 `xsi:*SchemaLocation` 的 XML，不应弹出，也不应产生错误。

## 排查

日志：`%TEMP%\XmlValueIntelliSense.log`

构建后核心逻辑可以用独立测试工程回归（不依赖 VS SDK）：

```powershell
dotnet run --project ..\XmlValueIntelliSense.Tests\XmlValueIntelliSense.Tests.csproj
```

测试工程默认校验 `SnibeMiddleware-New-Dev2\resource\update\DataBase\20190516_000000.xml`，也可以把 XML 路径作为第一个参数传入。

常见情况：

- 日志没有 `schema 已加载`：检查 `xsi:noNamespaceSchemaLocation` 路径是否相对 XML 文件正确、XSD 是否存在；
- 日志有 `schema 已加载` 但不弹：检查是否在引号内、XSD 里该属性是否真有 `xs:enumeration`；
- 没有日志：确认扩展已安装并重启 VS；确认文件是用 **XML (文本) 编辑器** 打开的。

## 已知边界

- 只加载本地 XSD（`file://` / 相对路径）；`http(s)://` 默认拒绝，避免打开文件时联网卡顿；
- `xs:pattern` 无法枚举候选，只能提供校验；
- `xs:anyAttribute` 不提供候选；
- 错误校验只覆盖“值被清空”的情况：XSD 中 `use="required"` 的属性，或带 `xs:enumeration` / `xs:boolean` 取值的属性，值为空时本扩展报“错误”；值非空但不在允许范围内仍由 VS 内置 XSD 校验提示（绿色波浪线）；
- VS 内置的 XSD 校验不会因为本扩展而消失，同一位置可能同时出现内置绿色波浪线和本扩展的红色错误波浪线；错误列表中的错误条目来自本扩展；
- 会替换 VS 内置的补全集合，保证不出现重复列表；如果同时安装了其他 XML 补全扩展，可能相互覆盖；
- 给已有文件添加 `xsi:noNamespaceSchemaLocation` 会改变文件字节，已纳入 DbChronicle 版本记录的文件请改用其他关联方式，避免 checksum 漂移。

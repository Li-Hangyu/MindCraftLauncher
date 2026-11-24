# Mind Craft Launcher

一个基于 **PCL2（Plain Craft Launcher 2）界面结构移植**的 WPF 桌面启动器：界面重画，内部逻辑收敛成可替换的服务接口，并新增两条联机通道——**陶瓦联机**（EasyTier 真实组网）与**红石联机**（Yggdrasil 外置登录 + 服务器列表），同时支持 **LabyMod 4 客户端下载**。

> **界面 2.0（本次更新）**：视觉全面重做——深色主题换成了「Deep Space」深空底 + 蓝→紫霓虹渐变强调色，左侧图标导航栏替代顶部标签条，自定义窗口外框（无边框 + 拖动 + 三个窗口按钮），启动页新增 Hero 主视觉与状态卡，全部输入控件（下拉框 / 复选框 / 单选框 / 滚动条 / 进度条）换成自定义模板，深浅两套主题键完全对齐，切页带淡入动画。视觉效果见 `docs/ui-preview.html`（浏览器直接打开）。
>
> **顺带修掉的报错隐患**：主题字典改用 pack URI 加载（原来相对 URI 在部分环境下会抛「无法找到资源」直接崩）；配置落盘失败不再把异常抛到 UI 线程；LabyMod 卡片「已安装」状态现在会刷新；LabyMod 列表读取加了防重入；程序集名去掉空格（pack URI / dotnet run 更稳）；新增全局异常兜底——任何未处理异常都会写日志到 `%APPDATA%\MindCraftLauncher\logs\` 并弹窗提示，而不是闪退。

```
MindCraftLauncher.sln
├─ src/MindCraftLauncher.Core      跨平台核心（net8.0）：下载源、版本安装、LabyMod、EasyTier、Yggdrasil、启动参数
├─ src/MindCraftLauncher.App       WPF 界面（net8.0-windows）：主窗口 + 5 个页面 + 主题
└─ tests/MindCraftLauncher.Tests   单元测试 + 联网冒烟测试（net8.0，零 NuGet 依赖）
```

## 一、环境要求

| 项目 | 要求 |
| --- | --- |
| 编译 | Visual Studio 2022（.NET 8 SDK）或 `dotnet` CLI 8.0+（`dotnet build -c Release` 已验证 0 警告 0 错误） |
| 运行 | Windows 10 / 11，.NET 8 Desktop Runtime |
| 陶瓦联机 | 额外需要 [EasyTier](https://easytier.cn) 命令行版（`easytier-core` / `easytier-cli`） |
| 红石联机 | 需要可访问的 Yggdrasil 认证服务器（红石类皮肤站） |

打开 `MindCraftLauncher.sln` 直接生成即可。`App` 工程里开了 `EnableWindowsTargeting`，在 Linux/macOS 上也能编译通过（只是跑不起来），便于 CI 校验。

## 二、快速开始

```powershell
# 编译整个解决方案
dotnet build -c Release

# 跑单元测试（48 项，无需联网）
dotnet run --project tests/MindCraftLauncher.Tests --nologo

# 额外跑联网冒烟测试（12 项，真实拉取 Mojang / BMCLAPI / Fabric / Forge / LabyMod / authlib-injector 接口校验解析器）
dotnet run --project tests/MindCraftLauncher.Tests --nologo -- --live
```

首次启动后建议顺序：**设置 → 选游戏目录和 Java → 保存** → **下载页开启本体下载并勾选确认 → 装一个版本** → **启动页点启动游戏**。

## 三、关于"改那一行"：本体下载开关

上游 PCL2 在 2026 年 10 月因合规原因关停了游戏本体下载。移植版没有把这个判断散落各处，而是收敛成**一个布尔开关**：

`src/MindCraftLauncher.Core/Config/FeatureFlags.cs`

```csharp
public static FeatureFlags Default() => new()
{
    GameDownloadEnabled = false,   // ← 就是这一行，改成 true 即默认放开
    LabyModDownloadEnabled = true,
    TerracottaLinkEnabled = true,
    RedstoneLinkEnabled = true,
};
```

两种放开方式：

1. **运行时**：设置页勾选「Minecraft 本体下载」+「已确认下载来源说明」，写回 `%APPDATA%\MindCraftLauncher\config.json`；下载页顶部也有同一组开关。
2. **代码里**：把上面那行改成 `true`（仍需确认位 `DownloadAcknowledgedAt` 非空，UI 首次会弹确认）。

未确认时调用下载会抛 `FeatureDisabledException`，界面给出提示而不是静默失败。

**必须说清楚的事实**：本程序下载走的是 Mojang 官方分发端点（`piston-meta.mojang.com` / `piston-data.mojang.com`）或其公开镜像 BMCLAPI（`bmclapi2.bangbang93.com`），文件本身不带任何绕过验证的处理；进入服务器与正版验证仍然依赖你自己的 Minecraft 账号。上游之所以关停，是合规压力而非技术故障——在你所在地区是否适合使用，请自行判断。

## 四、五个页面

| 页面 | 作用 | 关键实现 |
| --- | --- | --- |
| 启动 | 选账号/版本，拉起游戏，实时日志，可复制完整启动命令 | `Launch/GameLauncher.cs` |
| 版本 | **每个版本一张卡片**（选项卡式）：点卡片选中、启动、开目录、删除 |
| 下载 | 版本本体 + Fabric / Forge / OptiFine + LabyMod 4 |
| 整合包 | Modrinth 搜索安装 .mrpack，自动装加载器、解压 overrides |
| 模组 | Modrinth 搜索，jar 直接放进目标版本 mods | `Downloads/VersionInstaller.cs`、`Clients/ModLoaderService.cs`、`Clients/LabyModInstaller.cs` |
| 陶瓦联机 | EasyTier 真实组网 + 把局域网端口暴露给队友 | `EasyTier/EasyTierRunner.cs`、`Terracotta/TerracottaSession.cs` |
| 红石联机 | Yggdrasil 外置登录 + 服务器列表直连 | `Yggdrasil/YggdrasilClient.cs`、`AuthlibInjectorManager.cs` |
| 设置 | 路径、Java、内存、下载源、功能开关、EasyTier 位置、微软 client_id | `Config/LauncherConfig.cs` |

界面保留 PCL2 的「顶部标签条 + 右上大启动按钮 + 底部状态栏」结构，视觉按深浅两套主题重画（设置页可切换，`DynamicResource` 即时生效）。

## 五、陶瓦联机怎么跑通

1. 在【设置】指到 `easytier-core.exe`（点「自动查找」可扫描启动器目录 / 游戏目录 / PATH）。**创建虚拟网卡需要管理员权限**，若失败请以管理员身份重启启动器。
2. 房主：游戏里 `单人游戏 → 对局域网开放`，记下聊天框提示的端口号 → 陶瓦页填端口 → 点「开房」→ 复制连接码发给队友。
3. 加入方：陶瓦页选「我加入别人」→ 粘贴连接码 → 点「入网」。
4. 状态表里出现房主的 `10.x.x.x` 且 cost 是 `p2p` 后，「可连接地址」会自动填成 `房主虚拟IP:端口` → 点「用这个地址启动游戏」，或手动在多人游戏里粘贴。

连接码格式 `MCL1.<base64url(json)>`，内含网络名称、密钥、peer 列表与端口。**它等同房间密码，发给谁谁就能进你的虚拟网**。

## 六、红石联机（外置登录）

填认证服务器地址（`https://mcskin.cn` 这类站点首页，或带 `/api/yggdrasil` 的完整 API 根都可以，程序会规范化）→ 登录 → 账号进入列表 → 启动游戏时自动注入：

```
-javaagent:<启动器数据目录>/authlib-injector.jar=https://<站点>/api/yggdrasil
```

注入器 jar 由 `authlib-injector.yushi.moe/artifact/latest.json`（或其 BMCLAPI 镜像）获取，并按 **sha256** 校验。

服务器列表是启动器自有的 JSON（`servers.json`），通过 `--server/--port` 参数直连。**不改写游戏自带的 `servers.dat`**——那是 gzip NBT，写坏会毁掉玩家手动维护的列表。

## 七、LabyMod 4 下载

走官方发布接口（2026-11-01 实测可用，接口若变只改 `Core/Clients/LabyModApi.cs` 一处常量）：

```
频道列表   GET https://releases.r2.labymod.net/api/v1/channels.json                 → ["production","snapshot"]
频道清单   GET https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/{ch}/latest.json
主 jar     GET https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/{ch}/{commit}.jar
资源包 jar  GET https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{ch}/{commit}/{asset}/{checksum}.jar
版本描述   minecraftVersions[].customManifestUrl（完整 version json）
```

安装产物：版本列表新增 `{版本号}-LabyMod`，主 jar 落到 `libraries/net/labymod/LabyMod-4/`，资源包 jar 落到 `.minecraft/labymod-neo/assets/`，启动时把主 jar 追加进 classpath。

**已验证 / 待你在本机确认**：接口、清单结构、jar 可下载、sha1 校验都通过了自动化测试；LabyMod 的 classpath 注入方式（launchwrapper 入口 + 附加 jar）在真实游戏里是否完整生效，需要你在 Windows 上开一次游戏看菜单里有没有 LabyMod 项——如果没出现，最可能的调整点是版本 json 的 `mainClass` 与附加 jar 顺序，位置在 `Launch/ClassPathBuilder.cs` 与 `Clients/LabyModInstaller.cs`。

## 八、模组加载器（Fabric / Forge）

下载页的「模组加载器」卡片：选加载器类型 + 填游戏版本 → 读取列表 → 安装。装完版本列表里会多出一个新版本，启动页直接选它。

**Fabric**：不需要本机 Java。官方 meta 接口直接给出可用的 version json（`inheritsFrom` 基版本、主类 `net.fabricmc.loader.impl.launch.knot.KnotClient`），落盘 + 补齐 libraries 即完成；基版本的客户端 jar 会复制一份到新版本目录，classpath 才找得到。

**Forge**：必须在本机跑一次 Java——Forge 的 version json 由官方 installer 里的 processors 生成，没法凭空拼。实现是：

```
java -jar forge-<版本>-installer.jar --installClient     # 工作目录 = 游戏根目录
```

Java 版本按 MC 版本自动推断：1.16.5 及以下用 Java 8，1.17–1.20.4 用 Java 17，1.20.5 及以后用 Java 21（`ForgeInstaller.RequiredJavaMajor`，有单测覆盖）。安装器跑完后会按实际落盘结果找出版本 id，并再补一次 libraries（老版本 Forge 不会自己下）。安装器输出会原样进日志，退出码非 0 直接报错，不静默失败。

版本列表来自 `maven-metadata.xml`（全量）+ `promotions_slim.json`（标出「推荐」「最新」）。下载源选 BMCLAPI 时，Fabric meta 走 `/fabric-meta` 镜像、Forge 安装包走 `/maven` 镜像。

**OptiFine**：下载只能走 BMCLAPI 镜像（`/optifine/{mc}/{type}/{patch}` 直接返回 jar）——官网的下载链接带动态 token，直连拿不到文件。安装按目标版本的实际情况自动选路：

1. 本机已有同版本的 Forge 版本 → 把 OptiFine jar 放进那个版本的 `mods` 目录。这是 Forge 官方支持的用法，最稳，不跑任何安装器。
2. 只有纯净版 → 试一次官方安装器的无头入口 `java -cp <jar> optifine.Installer`，成功会在 versions 下生成带 OptiFine 的新版本并补齐依赖库；不成功就把 jar 路径和手动步骤抛给你（双击 jar → 目录填游戏根目录 → Install）。

路线 2 依赖 OptiFine 安装器的内部行为，**属未实测路径**，实机请先试一次。

**NeoForge / Quilt 未纳入**：NeoForge 的 maven 元数据接口在核实中取不到内容，不做猜测实现；要加的话在 `Clients/ModLoaderService.cs` 多注册一个 provider 即可，界面不用改。

## 九、版本页与整合包 / 模组

**版本页（独立界面）**：每个已安装版本是一张卡片，不再用下拉框。卡片上显示名称、加载器/整合包标签、Java 版本、安装时间，卡片里直接有「启动 / 目录 / 删除」。点卡片切换当前版本，右上角「启动游戏」和启动页都跟着这张卡片走。

**整合包页**：数据来自 Modrinth（公开接口，不需要 API Key）。搜索 → 点卡片选整合包 → 选版本 → 安装。流程是：下载 `.mrpack` → 读 `modrinth.index.json` → 按 `dependencies` 装好 MC 与加载器（fabric-loader / forge）→ 把加载器版本复制成独立版本 → 按 `files[].path` 下载每个模组 → 解压 `overrides/` 与 `client-overrides/` → 写 `packinfo.json` 标记。也支持从本地 `.mrpack` 安装。

**模组页**：选目标版本 + 游戏版本 + 加载器搜 Modrinth，选中后把 jar 放进该版本的 `mods` 目录。只装本体，不解析前置依赖。

**CurseForge 未接**：它的官方接口需要 API Key，遇到 CF 的 `.zip` 整合包会明确报"不支持"而不是假装装成功。NeoForge/Quilt 整合包同样会给出明确提示。

## 十、修掉的几个真实 bug

| 问题 | 影响 | 修法 |
| --- | --- | --- |
| 启动时不合并 `inheritsFrom` | **Fabric/Forge/整合包版本一启动就崩**（classpath 缺原版库） | 新增 `Launch/VersionResolver.cs`，启动前合并整条继承链 |
| 子版本没有客户端 jar 时 classpath 少一项 | 同上 | `ClassPathBuilder` 回落到父版本 jar |
| 导航按钮在 XAML 里 `IsChecked="True"` | 启动瞬间 `PageXxx` 字段还没赋值 → **空引用崩溃** | 改为 InitializeComponent 之后再设选中态，并加空保护 |
| 设置页保存被拒时界面开关不回滚 | 看起来像保存成功 | 回滚 VM 属性 |
| 版本列表「已安装」不刷新 | 装完仍显示未安装 | `VersionRow` 改为 `ObservableObject` 并通知 |
| `VersionManager.Delete` 会顺手建目录、空目录也返回成功 | 删除结果不可信 | 加路径校验、不创建目录、不存在即 false |

## 十一、微软正版登录

实现了设备码全流程（MS token → XBL → XSTS → Minecraft Services → 档案），但**需要你自己提供 `client_id`**：在 Azure 门户注册一个「桌面应用程序」，把应用程序(客户端)ID 填到设置页。没填时该入口给出明确提示，不影响离线账号与红石登录。这条链路涉及真实账号，沙箱里无法验证，属未实测代码。

## 十二、已验证的内容

- **48 项单元测试**全部通过（房间码往返、EasyTier 参数、CLI 表格解析、BMCLAPI 重写、功能开关门禁、版本 json 继承、参数展开、Yggdrasil/服务器列表/配置持久化、LabyMod 与 authlib-injector 解析、classpath 过滤、Fabric/Forge 地址拼装与版本过滤、Forge 所需 Java 推断、Fabric profile 落盘回读）。
- **12 项联网冒烟**通过（2026-11-01 / 11-11 实测数据）：
  - Mojang 清单 → 最新正式版 **26.3**（Java 25、114 个库、主类 `net.minecraft.client.main.Main`）
  - BMCLAPI 重写后的版本 json 可用
  - LabyMod **48.3 / commit e0629f72**，profile 主类 `net.minecraft.launchwrapper.Launch`、67 个库
  - authlib-injector **v1.2.8 build 56**（sha256 字段齐备）
  - 真实下载历史版本 `1.0` 客户端 jar（2307 KB）并通过 sha1 校验、按目录结构落盘
  - Fabric：MC **26.3** 对应 loader **0.20.1**，profile 主类 KnotClient、7 个库；BMCLAPI 的 `/fabric-meta` 镜像同样可用
  - Forge：maven 元数据共 **5105** 个版本，`1.20.1` 有 **138** 个，推荐版 **1.20.1-47.4.28**，安装包直链可达（8 MB）
  - OptiFine：BMCLAPI 镜像 `1.20.1` 返回 **14** 条，条目带 `type`/`patch`/`filename`/`forge`，jar 直链可用
  - Modrinth：整合包与模组搜索可用；真实拉取一个 `.mrpack`（169 KB）并解析出 `dependencies`（MC 26.3 + fabric-loader 0.20.1）与 `files[]`
- Core / App / Tests 三个工程在 .NET 8 SDK 下 `dotnet build -c Release` 全部编译通过（含 XAML 编译）。

## 十三、已知边界

- Fabric 安装已完整实现且不依赖 Java；Forge 走官方 installer 的 `--installClient`，**沙箱里没跑过真实安装**（需要 Windows + 对应 Java），逻辑与命令拼装已覆盖单测，实机结果需你确认。
- OptiFine 的「放进 Forge 版本 mods」路线是官方用法，风险低；「纯净版静默安装」路线未实测，失败会退回手动指引而不是装作成功。
- NeoForge / Quilt 未实现；快照版本可下载但没有做快照专属处理。
- 整合包安装里的 Forge 路线依赖官方 installer（同 Forge 一节，沙箱未实测）；`files[]` 是按序下载，没有做并发限流优化。
- 模组不解析前置依赖（Fabric API 等要自己补）；不检测模组冲突。
- 没有做整合包（CurseForge / Modrinth）导入，也没有 mod 搜索下载。
- EasyTier 的状态读取走 `easytier-cli peer` 的表格输出解析；若你使用的 EasyTier 版本改了输出格式，`PeerTableParser` 会解析不到行但不会崩，可切 `-r` RPC 端口方案（`EasyTierRunner.QueryStatusAsync` 传别的子命令）。
- 配置与 accessToken 是明文 JSON 存盘，共享电脑请手动删除 `%APPDATA%\MindCraftLauncher\accounts.json`。

## 十四、许可与致谢

基座界面结构参考 PCL2（龙腾猫跃，https://github.com/Meloong-Git/PCL ）；组网能力来自 EasyTier；外置登录来自 authlib-injector（yushijinhun）；下载镜像为 BMCLAPI（bangbang93）——按其使用要求，此处标明下载来源；LabyMod 4 客户端由 LabyMod 团队发布。本项目自身代码按 MIT 使用即可。

---

内容由 AI 生成

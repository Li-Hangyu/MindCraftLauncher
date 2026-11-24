using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Accounts;
using MindCraftLauncher.Core.Clients;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.EasyTier;
using MindCraftLauncher.Core.Launch;
using MindCraftLauncher.Core.Mods;
using MindCraftLauncher.Core.Versions;
using MindCraftLauncher.Core.Terracotta;
using MindCraftLauncher.Core.Yggdrasil;
using MindCraftLauncher.Tests;

// 只测不联网的部分：纯函数 + 本地文件读写。真实下载链路交给使用者在 Windows 上验证。
Environment.SetEnvironmentVariable("MINDCRAFTLAUNCHER_DATA",
    Path.Combine(Path.GetTempPath(), "mcl-data-" + Guid.NewGuid().ToString("N")[..8]));

TestRunner.Test("房间码：编码后能解出同样的组网参数", () =>
{
    var profile = new EasyTierProfile
    {
        NetworkName = "mc-terracotta-room",
        NetworkSecret = "s3cr3t-key",
        Dhcp = true,
        Peers = { "tcp://public.easytier.cn:11010" },
    };

    var code = RoomCode.Encode(profile, 51917);
    Assert.Contains("MCL1.", code);

    var decoded = RoomCode.Decode(code);
    Assert.Equal("mc-terracotta-room", decoded.NetworkName);
    Assert.Equal("s3cr3t-key", decoded.NetworkSecret);
    Assert.Equal(51917, decoded.LanPort);
    Assert.Count(1, decoded.Peers);
    Assert.Equal("tcp://public.easytier.cn:11010", decoded.Peers[0]);
    Assert.True(decoded.Dhcp);
});

TestRunner.Test("房间码：连接码里出现空格也不会破坏解析", () =>
{
    var code = RoomCode.Encode(new RoomPayload { NetworkName = "a", NetworkSecret = "b", Peers = { "tcp://x:1" }, Dhcp = false, Ipv4 = "10.0.0.7", LanPort = 23260 });
    var p = RoomCode.Decode(code);
    Assert.Equal("10.0.0.7", p.Ipv4);
    Assert.False(p.Dhcp);
});

TestRunner.Test("房间码：错误前缀要被拒", () =>
{
    var ex = Assert.Throws<FormatException>(() => RoomCode.Decode("HELLO.world"));
    Assert.Contains("MCL1.", ex.Message);
    Assert.True(RoomCode.TryDecode("MCL1.@@@bogus@@@", out var payload, out var err) == false);
    Assert.True(payload is null);
    Assert.NotNull(err);
});

TestRunner.Test("房间码：转成 profile 时缺 peers 会回落到公共节点", () =>
{
    var payload = new RoomPayload { NetworkName = "n", NetworkSecret = "s", Dhcp = true, LanPort = 1 };
    var profile = RoomCode.ToProfile(payload, "tcp://public.easytier.cn:11010");
    Assert.Count(1, profile.Peers);
    Assert.Equal("tcp://public.easytier.cn:11010", profile.Peers[0]);
});

TestRunner.Test("EasyTier 参数：DHCP + 公共节点 + 监听器", () =>
{
    var args = EasyTierArgs.Build(new EasyTierProfile
    {
        NetworkName = "abc",
        NetworkSecret = "abc",
        Dhcp = true,
        Peers = { "tcp://public.easytier.cn:11010" },
        Listeners = { "udp://0.0.0.0:11010" },
        Hostname = "mcl-host",
    });

    Assert.Sequence(new[]
    {
        "--network-name", "abc", "--network-secret", "abc", "-d",
        "-p", "tcp://public.easytier.cn:11010",
        "-l", "udp://0.0.0.0:11010",
        "--hostname", "mcl-host",
    }, args);
});

TestRunner.Test("EasyTier 参数：指定静态虚拟 IP 时用 -i 而不是 -d", () =>
{
    var args = EasyTierArgs.Build(new EasyTierProfile { NetworkName = "n", NetworkSecret = "s", Dhcp = false, Ipv4 = "10.144.144.1" });
    Assert.Contains("-i", string.Join(' ', args));
    Assert.Contains("10.144.144.1", string.Join(' ', args));
    Assert.DoesNotContain("-d", string.Join(' ', args));
});

TestRunner.Test("EasyTier 参数：空名称/空密钥要立刻报错", () =>
{
    Assert.Throws<ArgumentException>(() => EasyTierArgs.Build(new EasyTierProfile { NetworkName = "", NetworkSecret = "x" }));
    Assert.Throws<ArgumentException>(() => EasyTierArgs.Build(new EasyTierProfile { NetworkName = "n", NetworkSecret = "" }));
    Assert.Throws<ArgumentException>(() => EasyTierArgs.Build(new EasyTierProfile { NetworkName = "n", NetworkSecret = "s", Dhcp = false, Ipv4 = "10.10" }));
});

TestRunner.Test("EasyTier 参数：命令行展示可读", () =>
{
    var line = EasyTierArgs.ToCommandLine(@"C:\Tools\easy tier\easytier-core.exe",
        new EasyTierProfile { NetworkName = "n", NetworkSecret = "s", Dhcp = true });
    Assert.Contains("\"C:\\Tools\\easy tier\\easytier-core.exe\"", line);
    Assert.Contains("--network-name n", line);
});

TestRunner.Test("easytier-cli 表格：按表头取列并识别本机", () =>
{
    const string output = """
        | ipv4 | hostname | cost | lat_ms | loss_rate | rx_bytes | tx_bytes | tunnel_proto | nat_type | id | version |
        | ------------ | -------------- | ----- | ------ | --------- | -------- | -------- | ------------ | -------- | ---------- | --------------- |
        | 10.144.144.1 | mcl-host | Local | * | * | * | * | udp | FullCone | 439804259 | 2.4.2 |
        | 10.144.144.2 | friend-pc | p2p | 3.452 | 0 | 17.33 kB | 20.42 kB | udp | FullCone | 390879727 | 2.4.2 |
        | 10.144.144.3 | relay-node | relay | 88.1 | 1 | 1 kB | 2 kB | tcp | Symmetric | 111 | 2.4.2 |
        """;

    var status = PeerTableParser.Parse(output, running: true);
    Assert.Count(3, status.Peers);
    Assert.Equal("10.144.144.1", status.LocalIpv4);
    Assert.True(status.Peers[0].IsSelf);
    Assert.True(status.Peers[1].IsDirect);
    Assert.False(status.Peers[2].IsDirect);   // relay 中转不算直连
    Assert.Equal("3.452", status.Peers[1].LatencyMs);
});

TestRunner.Test("easytier-cli 表格：列顺序变化也能解析", () =>
{
    const string output = """
        | hostname | cost | ipv4 |
        | ---------- | ---- | ---------- |
        | pc-a | Local | 10.0.0.1 |
        """;
    var s = PeerTableParser.Parse(output, true);
    Assert.Count(1, s.Peers);
    Assert.Equal("10.0.0.1", s.Peers[0].Ipv4);
});

TestRunner.Test("陶瓦地址：房主取自己虚拟IP，加入方取队友IP", () =>
{
    var runner = new EasyTierRunner();
    var session = new TerracottaSession(runner, new FeatureFlags { TerracottaLinkEnabled = true });

    var peers = new List<EasyTierPeer>
    {
        new("10.144.144.1", "me", "Local", "*", "*", "*", "*", "udp", "FullCone", "1"),
        new("10.144.144.9", "friend", "p2p", "5", "0", "1", "2", "udp", "FullCone", "2"),
    };
    var status = new EasyTierStatus(true, peers, "10.144.144.1", "");

    var hostType = typeof(TerracottaSession);
    var build = hostType.GetMethod("BuildAddress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var role = hostType.GetField("_role", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var port = hostType.GetField("_lanPort", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert.NotNull(build);

    role!.SetValue(session, TerracottaRole.Host);
    port!.SetValue(session, 51917);
    Assert.Equal("10.144.144.1:51917", (string)build!.Invoke(session, new object[] { status, "10.144.144.1" })!);

    role.SetValue(session, TerracottaRole.Joiner);
    Assert.Equal("10.144.144.9:51917", (string)build.Invoke(session, new object[] { status, "10.144.144.1" })!);
});

TestRunner.Test("陶瓦：端口非法要拒绝，功能开关关闭要抛 FeatureDisabled", () =>
{
    var flags = new FeatureFlags { TerracottaLinkEnabled = false };
    var session = new TerracottaSession(new EasyTierRunner(), flags);
    Assert.Throws<FeatureDisabledException>(() => session.StartHost("n", "s", 25565, "tcp://x:1"));

    var session2 = new TerracottaSession(new EasyTierRunner(), new FeatureFlags { TerracottaLinkEnabled = true });
    Assert.Throws<ArgumentException>(() => session2.StartHost("n", "s", 0, "tcp://x:1"));
    Assert.Throws<ArgumentException>(() => session2.StartHost("n", "s", 70000, "tcp://x:1"));
});

TestRunner.Test("下载源：BMCLAPI 域名重写规则", () =>
{
    var src = new BmclApiDownloadSource();
    Assert.Equal("https://bmclapi2.bangbang93.com/v1/packages/abc/1.20.1.json",
        src.RewriteUrl("https://piston-meta.mojang.com/v1/packages/abc/1.20.1.json"));
    Assert.Equal("https://bmclapi2.bangbang93.com/assets/c0/17cde8",
        src.RewriteUrl("https://resources.download.minecraft.net/c0/17cde8"));
    Assert.Equal("https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
        src.RewriteUrl("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"));
    Assert.Equal("https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json",
        src.RewriteUrl("https://authlib-injector.yushi.moe/artifact/latest.json"));
    Assert.Equal("https://meta.fabricmc.net/v2/versions/loader", src.RewriteUrl("https://meta.fabricmc.net/v2/versions/loader"),
        "不该被改写的地址要原样返回");
});

TestRunner.Test("下载源：注册表按 id 挑选，未知 id 回落到官方", () =>
{
    var reg = new DownloadSourceRegistry();
    Assert.Equal("bmclapi", reg.Pick("bmclapi").Id);
    Assert.Equal("mojang", reg.Pick("BMCLAPI-x").Id);
    Assert.Equal("mojang", reg.Pick(null).Id);
});

TestRunner.Test("功能开关：本体下载默认关闭，确认后放开", () =>
{
    var flags = FeatureFlags.Default();
    Assert.False(flags.GameDownloadEnabled);
    Assert.Throws<FeatureDisabledException>(() => flags.EnsureGameDownload());

    flags.GameDownloadEnabled = true;
    Assert.Throws<FeatureDisabledException>(() => flags.EnsureGameDownload());   // 还没确认过

    flags.DownloadAcknowledgedAt = new DateTime(2026, 11, 1, 12, 0, 0);
    flags.EnsureGameDownload();
});

TestRunner.Test("maven 坐标转路径", () =>
{
    Assert.Equal("com/google/guava/guava/21.0/guava-21.0.jar", MavenPath.FromCoordinate("com.google.guava:guava:21.0"));
    Assert.Equal("org/lwjgl/lwjgl-natives-windows/3.3.3/lwjgl-natives-windows-3.3.3-natives-windows.jar",
        MavenPath.FromCoordinate("org.lwjgl:lwjgl-natives-windows:3.3.3", "natives-windows"));
});

TestRunner.Test("规则过滤：只保留当前系统的库", () =>
{
    var windowsOnly = new List<RuleRef> { new() { Action = "allow", Os = new OsMatchRef { Name = "windows" } } };
    Assert.Equal(true, RuleEvaluator.Allows(windowsOnly, "windows"));
    Assert.Equal(false, RuleEvaluator.Allows(windowsOnly, "osx"));
    Assert.Equal(true, RuleEvaluator.Allows(null, "linux"));
});

TestRunner.Test("参数展开：${} 占位与 rules 对象形式", () =>
{
    using var doc = System.Text.Json.JsonDocument.Parse("""
        ["--username", "${auth_player_name}",
         {"rules":[{"action":"allow","os":{"name":"linux"}}],"value":["-XX:+UseZGC"]},
         {"rules":[{"action":"allow","os":{"name":"windows"}}],"value":["--enable-new-arguments"]},
         {"value":["--a","--b"]}]
        """);
    var items = new List<System.Text.Json.JsonElement>();
    foreach (var e in doc.RootElement.EnumerateArray()) items.Add(e);

    var linux = ArgumentExpander.Expand(items, "linux", new Dictionary<string, string> { ["auth_player_name"] = "Steve" });
    Assert.Sequence(new[] { "--username", "Steve", "-XX:+UseZGC", "--a", "--b" }, linux);

    var win = ArgumentExpander.Expand(items, "windows", new Dictionary<string, string> { ["auth_player_name"] = "Steve" });
    Assert.Sequence(new[] { "--username", "Steve", "--enable-new-arguments", "--a", "--b" }, win);
});

TestRunner.Test("版本 json 继承：子版本覆盖主类，libraries 追加", () =>
{
    var parent = new VersionJson
    {
        Id = "1.20.1",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = { new LibraryRef { Name = "a:b:1" } },
    };
    var child = new VersionJson
    {
        Id = "1.20.1-forge",
        MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        InheritsFrom = "1.20.1",
        Libraries = { new LibraryRef { Name = "c:d:2" } },
    };

    var merged = VersionInstaller.Merge(parent, child);
    Assert.Equal("1.20.1-forge", merged.Id);
    Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
    Assert.Null(merged.InheritsFrom);
    Assert.Count(2, merged.Libraries);
});

TestRunner.Test("assets 索引解析：objects 里的 hash 全部取出来", () =>
{
    const string index = """
        {"objects":{"minecraft/sounds/a.ogg":{"hash":"aaaa","size":1},"minecraft/lang/en_us.json":{"hash":"bbbb","size":2},"bad":{"size":3}}}
        """;
    var list = VersionInstaller.LoadAssetObjectsFromJson(index);
    Assert.Count(2, list);
    Assert.Equal("aaaa", list[0].Value);
    Assert.Equal("minecraft/lang/en_us.json", list[1].Key);
});

TestRunner.Test("Yggdrasil：认证地址规范化", () =>
{
    Assert.Equal("https://mcskin.cn/api/yggdrasil", JavaAgentArgument.NormalizeUrl("https://mcskin.cn/"));
    Assert.Equal("https://mcskin.cn/api/yggdrasil", JavaAgentArgument.NormalizeUrl("https://mcskin.cn/api/yggdrasil"));
});

TestRunner.Test("Yggdrasil：-javaagent 参数拼接（含空格路径）", () =>
{
    var plain = JavaAgentArgument.Build(@"C:\MCL\authlib-injector.jar", "https://mcskin.cn");
    Assert.Equal(@"-javaagent:C:\MCL\authlib-injector.jar=https://mcskin.cn/api/yggdrasil", plain);

    var spaced = JavaAgentArgument.Build(@"C:\Program Files\MCL\authlib-injector.jar", "https://mcskin.cn");
    Assert.Contains("\"C:\\Program Files\\MCL\\authlib-injector.jar\"", spaced);
});

TestRunner.Test("Yggdrasil：会话里的 uuid 去掉横杠", () =>
{
    const string json = """
        {"accessToken":"tok","clientToken":"cli","selectedProfile":{"id":"8e9f-1c2d-4a5b-6c7d-8e9f0c1d2e3f","name":"Redstone"},"availableProfiles":[]}
        """;
    var s = JsonOpt.Deserialize<YggdrasilSession>(json)!;
    Assert.Equal("Redstone", s.PlayerName);
    Assert.Contains("8e9f1c2d4a5b6c7d8e9f0c1d2e3f", s.UuidDashless);
});

TestRunner.Test("服务器列表：地址解析支持 ip:port 与裸域名", () =>
{
    var a = ServerListStore.Parse("房内", "10.144.144.2:51917");
    Assert.Equal("10.144.144.2", a.Address);
    Assert.Equal(51917, a.Port);

    var b = ServerListStore.Parse("公网", "play.example.com");
    Assert.Equal("play.example.com", b.Address);
    Assert.Equal(25565, b.Port);

    var c = ServerListStore.Parse("ipv6", "[2001:db8::1]:25565");
    Assert.Equal("[2001:db8::1]", c.Address);
    Assert.Equal(25565, c.Port);
});

TestRunner.Test("服务器列表：本地持久化读写", () =>
{
    using var tmp = new TempDir();
    var store = new ServerListStore(tmp.Path);
    var e = store.Add(new ServerEntry { Name = "s1", Address = "10.0.0.2", Port = 1234 });
    Assert.Contains("10.0.0.2:1234", e.HostPort);

    var reopened = new ServerListStore(tmp.Path);
    Assert.Count(1, reopened.All);
    Assert.Equal("s1", reopened.All[0].Name);

    reopened.Touch(e.Id);
    Assert.NotNull(new ServerListStore(tmp.Path).All[0].LastConnectedAt);
    Assert.True(reopened.Remove(e.Id));
});

TestRunner.Test("离线账号 UUID 稳定且与玩家名绑定", () =>
{
    var a = LauncherAccount.Offline("Steve");
    var b = LauncherAccount.Offline("Steve");
    Assert.Equal(a.Uuid, b.Uuid);
    Assert.NotEqual(a.Uuid, LauncherAccount.Offline("Alex").Uuid);
    Assert.Equal(32, a.Uuid.Length);
});

TestRunner.Test("配置持久化：功能开关与路径写回", () =>
{
    using var tmp = new TempDir();
    var store = new ConfigStore(tmp.Path);
    var cfg = store.Load();
    cfg.DownloadSourceId = "bmclapi";
    cfg.Flags.GameDownloadEnabled = true;
    cfg.Flags.DownloadAcknowledgedAt = new DateTime(2026, 11, 1);
    store.Save(cfg);

    var again = new ConfigStore(tmp.Path).Load();
    Assert.Equal("bmclapi", again.DownloadSourceId);
    Assert.True(again.Flags.GameDownloadEnabled);
    Assert.NotNull(again.Flags.DownloadAcknowledgedAt);
});

TestRunner.Test("LabyMod 接口地址拼装", () =>
{
    Assert.Equal("https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/production/latest.json",
        LabyModApi.ManifestUrl("production"));
    Assert.Equal("https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/production/e0629f72.jar",
        LabyModApi.ClientJarUrl("production", "e0629f72"));
    Assert.Equal("https://releases.r2.labymod.net/api/v1/download/assets/labymod4/production/e0629f72/common/740d75.jar",
        LabyModApi.AssetJarUrl("production", "e0629f72", "common", "740d75"));
});

TestRunner.Test("LabyMod 清单：按 MC 版本标签查找", () =>
{
    const string json = """
        {"labyModVersion":"48.3","commitReference":"e0629f72","sha1":"c9d7","size":40054088,
         "assets":{"common":"740d","fonts":"ff48"},
         "minecraftVersions":[{"tag":"26.3","version":"26.3","customManifestUrl":"https://x/26.3.json"},
                               {"tag":"1.16.5","version":"1.16.5","customManifestUrl":"https://x/1.16.5.json"}]}
        """;
    var m = JsonOpt.Deserialize<LabyModManifest>(json)!;
    Assert.Equal("48.3", m.LabyModVersion);
    Assert.Equal("https://x/1.16.5.json", m.FindMcVersion("1.16.5")!.CustomManifestUrl);
    Assert.True(m.FindMcVersion("1.12.2") is null);
    Assert.Count(2, m.Assets);
});

TestRunner.Test("authlib-injector 工件清单解析", () =>
{
    const string json = """
        {"build_number":56,"version":"1.2.8","release_time":"2026-07-11T17:14:13Z",
         "download_url":"https://authlib-injector.yushi.moe/artifact/56/authlib-injector-1.2.8.jar",
         "checksums":{"sha256":"9c7f43"}}
        """;
    var a = JsonOpt.Deserialize<AuthlibInjectorArtifact>(json)!;
    Assert.Equal(56, a.BuildNumber);
    Assert.Equal("1.2.8", a.Version);
    Assert.Equal("9c7f43", a.Checksum!.Sha256);
});

TestRunner.Test("java -version 输出解析主版本", () =>
{
    Assert.Equal(17, JavaLocator.ParseMajor("openjdk version \"17.0.9\" 2023-10-17"));
    Assert.Equal(8, JavaLocator.ParseMajor("java version \"1.8.0_371\""));
    Assert.Equal(25, JavaLocator.ParseMajor("openjdk version \"25.0.1\" 2026-01-01"));
    Assert.Equal(0, JavaLocator.ParseMajor(""));
});

TestRunner.Test("Java 主版本按对应游戏规则归一化", () =>
{
    Assert.Equal(8, JavaLocator.NormalizeRequiredMajor(8));
    Assert.Equal(17, JavaLocator.NormalizeRequiredMajor(16));
    Assert.Equal(17, JavaLocator.NormalizeRequiredMajor(17));
    Assert.Equal(21, JavaLocator.NormalizeRequiredMajor(21));
    Assert.Equal(25, JavaLocator.NormalizeRequiredMajor(25));
});

TestRunner.Test("classpath 组装：过滤不匹配系统的库并按分隔符拼接", () =>
{
    using var tmp = new TempDir();
    var paths = new GamePaths(tmp.Path);

    var vjar = paths.VersionJar("1.20.1");
    File.WriteAllText(vjar, "x");
    var lib = Path.Combine(paths.Libraries, "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
    File.WriteAllText(lib, "x");
    var missing = Path.Combine(paths.Libraries, "only", "windows", "1", "windows-1.jar");

    var json = new MindCraftLauncher.Core.Downloads.VersionJson
    {
        Id = "1.20.1",
        Libraries =
        {
            new LibraryRef { Name = "org.lwjgl:lwjgl:3.3.3", Downloads = new LibraryDownloads { Artifact = new FileRef { Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar" } } },
            new LibraryRef { Name = "only:windows:1", Rules = { new RuleRef { Action = "allow", Os = new OsMatchRef { Name = "windows" } } }, Downloads = new LibraryDownloads { Artifact = new FileRef { Path = "only/windows/1/windows-1.jar" } } },
        },
    };

    var cp = ClassPathBuilder.Build(paths, json, new[] { missing }, "linux");
    Assert.Contains(vjar, cp);
    Assert.Contains("lwjgl-3.3.3.jar", cp);
    Assert.DoesNotContain("windows-1.jar", cp);
});


TestRunner.Test("Fabric 接口地址拼装与镜像替换", () =>
{
    Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.20.1", ModLoaderApi.FabricLoadersFor("1.20.1"));
    Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.20.1/0.16.0/profile/json",
        ModLoaderApi.FabricProfile("1.20.1", "0.16.0"));
    Assert.Equal("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader",
        ModLoaderApi.FabricMirror(ModLoaderApi.FabricLoaderVersions));
});

TestRunner.Test("Forge 版本号拆分与按 MC 版本过滤", () =>
{
    Assert.True(ModLoaderApi.TrySplitForgeVersion("1.20.1-47.4.0", out var g, out var t));
    Assert.Equal("1.20.1", g);
    Assert.Equal("47.4.0", t);
    Assert.False(ModLoaderApi.TrySplitForgeVersion("47.4.0", out _, out _));

    var filtered = ModLoaderApi.FilterByGameVersion(
        new[] { "1.20.1-47.4.0", "1.20.1-47.2.0", "1.19.2-43.4.8", "26.3-66.1.2" }, "1.20.1");
    Assert.Sequence(new[] { "1.20.1-47.4.0", "1.20.1-47.2.0" }, filtered);
});

TestRunner.Test("Forge maven-metadata 解析：倒序返回", () =>
{
    const string xml = """
        <metadata><versioning><latest>26.3-66.1.2</latest><versions>
        <version>1.20.1-47.2.0</version><version>1.20.1-47.4.0</version><version>26.3-66.1.2</version>
        </versions></versioning></metadata>
        """;
    var v = ModLoaderApi.ParseMavenMetadataVersions(xml);
    Assert.Sequence(new[] { "26.3-66.1.2", "1.20.1-47.4.0", "1.20.1-47.2.0" }, v);
});

TestRunner.Test("Forge 推荐版 / 最新版取自 promotions_slim", () =>
{
    var promos = new PromotionsSlim { Promos = { ["1.20.1-recommended"] = "47.2.0", ["1.20.1-latest"] = "47.4.0" } };
    Assert.Equal("1.20.1-47.2.0", ModLoaderApi.RecommendedVersion(promos, "1.20.1"));
    Assert.Equal("1.20.1-47.4.0", ModLoaderApi.LatestVersion(promos, "1.20.1"));
    Assert.True(ModLoaderApi.RecommendedVersion(promos, "1.8.9") is null);
    Assert.True(ModLoaderApi.RecommendedVersion(null, "1.20.1") is null);
});

TestRunner.Test("Forge 尾号排序：47.4.0 排在 47.2.30 前面", () =>
{
    Assert.True(ModLoaderApi.CompareForgeParts("47.4.0", "47.2.30") < 0);
    Assert.True(ModLoaderApi.CompareForgeParts("47.2.30", "47.4.0") > 0);
    Assert.Equal(0, ModLoaderApi.CompareForgeParts("47.2.0", "47.2.0"));
});

TestRunner.Test("Forge 安装器直链与镜像", () =>
{
    Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar",
        ModLoaderApi.ForgeInstallerJar("1.20.1-47.4.0"));
    Assert.Contains("bmclapi2.bangbang93.com/maven", ModLoaderApi.ForgeInstallerJarMirror("1.20.1-47.4.0"));
});

TestRunner.Test("Forge 安装器所需 Java 版本按 MC 版本推断", () =>
{
    Assert.Equal(8, ForgeInstaller.RequiredJavaMajor("1.12.2"));
    Assert.Equal(8, ForgeInstaller.RequiredJavaMajor("1.16.5"));
    Assert.Equal(17, ForgeInstaller.RequiredJavaMajor("1.18.2"));
    Assert.Equal(17, ForgeInstaller.RequiredJavaMajor("1.20.1"));
    Assert.Equal(21, ForgeInstaller.RequiredJavaMajor("1.21.1"));
    Assert.Equal(21, ForgeInstaller.RequiredJavaMajor("26.3"));
});

TestRunner.Test("Fabric loader 响应解析", () =>
{
    const string json = """
        [{"loader":{"version":"0.16.0","stable":true,"maven":"net.fabricmc:fabric-loader:0.16.0"},
          "intermediary":{"version":"1.20.1","stable":true,"maven":"net.fabricmc:intermediary:1.20.1"}}]
        """;
    var list = JsonOpt.Deserialize<List<FabricLoaderForVersion>>(json)!;
    Assert.Count(1, list);
    Assert.Equal("0.16.0", list[0].Loader.Version);
    Assert.True(list[0].Loader.Stable);
    Assert.Equal("1.20.1", list[0].Intermediary.Version);
});

TestRunner.Test("Fabric 版本 json：profile 落盘后能被版本仓库读到", () =>
{
    using var tmp = new TempDir();
    var paths = new GamePaths(tmp.Path);
    const string profile = """
        {"id":"fabric-loader-0.16.0-1.20.1","inheritsFrom":"1.20.1","type":"release",
         "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
         "arguments":{"game":[],"jvm":["-DFabricMcEmu= net.minecraft.client.main.Main "]},
         "libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0","url":"https://maven.fabricmc.net"}]}
        """;
    var json = JsonOpt.Deserialize<VersionJson>(profile)!;

    var flags = new FeatureFlags { GameDownloadEnabled = true, DownloadAcknowledgedAt = DateTime.Now };
    var installer = new VersionInstaller(new HttpFetcher(), paths, new MojangDownloadSource(), flags);
    installer.WriteVersionJson(json);

    var repo = new InstalledVersionRepository(paths);
    var loaded = repo.Load("fabric-loader-0.16.0-1.20.1");
    Assert.NotNull(loaded);
    Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", loaded!.MainClass);
    Assert.Equal("1.20.1", loaded.InheritsFrom);
    Assert.Count(1, loaded.Libraries);
});

TestRunner.Test("OptiFine 接口地址拼装", () =>
{
    Assert.Equal("https://bmclapi2.bangbang93.com/optifine/1.20.1", OptiFineInstaller.ListUrl("1.20.1"));
    Assert.Equal("https://bmclapi2.bangbang93.com/optifine/1.20.1/HD_U/I6",
        OptiFineInstaller.JarUrl("1.20.1", "HD_U", "I6"));
});

TestRunner.Test("OptiFine 版本标识拆分与排序键", () =>
{
    var (type, patch) = OptiFineInstaller.SplitTypePatch("HD_U/I6");
    Assert.Equal("HD_U", type);
    Assert.Equal("I6", patch);
    Assert.Throws<ArgumentException>(() => OptiFineInstaller.SplitTypePatch("I6"));

    var a = OptiFineInstaller.SplitPatch("I6");
    var b = OptiFineInstaller.SplitPatch("I5");
    Assert.Equal("I", a.Letters);
    Assert.True(a.Number > b.Number, "I6 应排得比 I5 新");

    var pre = OptiFineInstaller.SplitPatch("pre4");
    Assert.Equal("PRE", pre.Letters);
    Assert.Equal(4, pre.Number);
});

TestRunner.Test("OptiFine 清单解析：字段与显示名", () =>
{
    const string json = """
        [{"mcversion":"1.20.1","type":"HD_U","patch":"I6","filename":"OptiFine_1.20.1_HD_U_I6.jar","forge":"Forge 47.2.18"},
         {"mcversion":"1.20.1","type":"HD_U","patch":"pre4","filename":"OptiFine_1.20.1_HD_U_pre4.jar"}]
        """;
    var list = JsonOpt.Deserialize<List<OptiFineEntry>>(json)!;
    Assert.Count(2, list);
    Assert.Equal("I6", list[0].Patch);
    Assert.Contains("OptiFine HD_U I6", list[0].Display);
    Assert.Contains("Forge 47.2.18", list[0].Display);
    Assert.Contains("OptiFine HD_U pre4", list[1].Display);
});

TestRunner.Test("Modrinth facets 与地址拼装", () =>
{
    Assert.Equal("[[\"project_type:mod\"]]", ModrinthApi.BuildFacets("mod", null, null));
    Assert.Equal("[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:fabric\"]]",
        ModrinthApi.BuildFacets("mod", "1.20.1", "fabric"));

    var url = ModrinthApi.SearchUrl("sodium", "mod", "1.20.1", "fabric");
    Assert.Contains("/v2/search?query=sodium", url);
    Assert.Contains("limit=20", url);
    Assert.Contains("facets=", url);

    var vurl = ModrinthApi.VersionsUrl("AANobbMI", "1.20.1", "fabric");
    Assert.Contains("/v2/project/AANobbMI/version?", vurl);
    Assert.Contains("game_versions=", vurl);
    Assert.Contains("loaders=", vurl);
});

TestRunner.Test("mrpack 索引解析：依赖与客户端文件过滤", () =>
{
    const string json = """
    {"formatVersion":1,"game":"minecraft","versionId":"15.0.0","name":"Fabulously Optimized",
     "dependencies":{"minecraft":"26.3","fabric-loader":"0.20.1"},
     "files":[
       {"path":"mods/a.jar","fileSize":1,"downloads":["https://cdn/a.jar"],"env":{"client":"required","server":"required"}},
       {"path":"mods/b.jar","fileSize":1,"downloads":["https://cdn/b.jar"],"env":{"client":"unsupported","server":"required"}},
       {"path":"mods/c.jar","fileSize":1,"downloads":["https://cdn/c.jar"]}]}
    """;
    var idx = JsonOpt.Deserialize<ModpackIndex>(json)!;
    Assert.Equal("26.3", idx.MinecraftVersion);
    Assert.Equal("0.20.1", idx.FabricLoader);
    Assert.True(idx.ForgeVersion is null);
    Assert.Count(2, idx.ClientFiles);
    Assert.Equal("mods/a.jar", idx.ClientFiles.First().Path);
});

TestRunner.Test("启动前合并 inheritsFrom（修 Fabric/Forge 启动崩溃）", () =>
{
    using var tmp = new TempDir();
    var paths = new GamePaths(tmp.Path);

    File.WriteAllText(paths.VersionJson("1.20.1"), JsonOpt.Serialize(new VersionJson
    {
        Id = "1.20.1",
        MainClass = "net.minecraft.client.main.Main",
        Type = "release",
        Libraries = { new LibraryRef { Name = "org.lwjgl:lwjgl:3.3.3" } },
    }));
    File.WriteAllText(paths.VersionJson("fabric-loader-0.16.0-1.20.1"), JsonOpt.Serialize(new VersionJson
    {
        Id = "fabric-loader-0.16.0-1.20.1",
        InheritsFrom = "1.20.1",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = { new LibraryRef { Name = "net.fabricmc:fabric-loader:0.16.0" } },
    }));

    var merged = VersionResolver.Resolve(paths, "fabric-loader-0.16.0-1.20.1");
    Assert.NotNull(merged);
    Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged!.MainClass);
    Assert.Null(merged.InheritsFrom);
    Assert.Count(2, merged.Libraries);
    Assert.Equal("1.20.1", merged.Jar, "客户端 jar 回落到父版本");
});

TestRunner.Test("版本名清理与删除保护", () =>
{
    Assert.Equal("My-Pack-1", VersionManager.SanitizeName("My Pack 1"));
    Assert.Equal("pack", VersionManager.SanitizeName("   "));
    Assert.False(VersionManager.SanitizeName("a/b:c").Contains('/'));

    using var tmp = new TempDir();
    var manager = new VersionManager(new GamePaths(tmp.Path));
    Assert.False(manager.Delete(""));
    Assert.False(manager.Delete("../escape"));
});

var unitResult = TestRunner.Run(args.FirstOrDefault(a => !a.StartsWith("-")));

if (args.Contains("--live"))
{
    Console.WriteLine();
    Console.WriteLine("== 联网冒烟测试 ==");
    var liveResult = await LiveTests.RunAsync();
    Environment.Exit(unitResult == 0 && liveResult == 0 ? 0 : 1);
}

Environment.Exit(unitResult);

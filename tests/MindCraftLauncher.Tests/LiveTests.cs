using MindCraftLauncher.Core.Clients;
using File = System.IO.File;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.EasyTier;
using MindCraftLauncher.Core.Yggdrasil;

namespace MindCraftLauncher.Tests;

/// <summary>
/// 联网冒烟测试：<c>dotnet run --project tests/MindCraftLauncher.Tests -- --live</c> 才会跑。
/// 目的是用真实远端数据验证解析器与 URL 拼装，而不是只看本地字符串。
/// 任一端点不可达时按"跳过"处理，不影响退出码（镜像源会受网络波动影响）。
/// </summary>
public static class LiveTests
{
    public static async Task<int> RunAsync()
    {
        using var http = new HttpFetcher(timeout: TimeSpan.FromSeconds(25));

        await Case("Mojang 版本清单可解析", async () =>
        {
            var m = await http.GetJsonAsync<VersionManifest>(MojangDownloadSource.ManifestV2);
            Expect(m.Versions.Count > 500, "版本条目过少，清单结构可能已变");
            Expect(!string.IsNullOrEmpty(m.Latest.Release), "缺少 latest.release");
            var sample = m.Versions.First(v => v.Id == m.Latest.Release);
            var json = await http.GetJsonAsync<VersionJson>(sample.Url, new MojangDownloadSource());
            Expect(json.Downloads.Client is not null, "缺少 downloads.client");
            Expect(json.JavaVersion.MajorVersion >= 17, "最新版的 Java 需求异常：" + json.JavaVersion.MajorVersion);
            Console.WriteLine($"        最新正式版 {m.Latest.Release}（Java {json.JavaVersion.MajorVersion}，" +
                              $"库 {json.Libraries.Count} 个，主类 {json.MainClass}）");
        });

        await Case("BMCLAPI 镜像可用（重写后返回同一版本）", async () =>
        {
            var m = await http.GetJsonAsync<VersionManifest>(MojangDownloadSource.ManifestV2);
            var entry = m.Versions.First(v => v.Id == m.Latest.Release);
            var bmcl = new BmclApiDownloadSource();
            var rewritten = bmcl.RewriteUrl(entry.Url);
            var json = await http.GetJsonAsync<VersionJson>(rewritten, null);
            Expect(json.Id == entry.Id, "镜像返回的版本 id 与官方不一致");
            Console.WriteLine($"        {rewritten}");
        });

        await Case("LabyMod 频道与清单可解析", async () =>
        {
            var channels = await http.GetJsonAsync<List<string>>(LabyModApi.ChannelsUrl);
            Expect(channels.Contains("production"), "频道列表里没有 production");
            var manifest = await http.GetJsonAsync<LabyModManifest>(LabyModApi.ManifestUrl("production"));
            Expect(manifest.MinecraftVersions.Count > 3, "支持的 MC 版本过少");
            Expect(manifest.Assets.ContainsKey("common"), "assets 里缺 common");
            var first = manifest.MinecraftVersions[0];
            var profile = await http.GetJsonAsync<VersionJson>(first.CustomManifestUrl);
            Expect(!string.IsNullOrEmpty(profile.MainClass), "LabyMod profile 缺 mainClass");
            Console.WriteLine($"        LabyMod {manifest.LabyModVersion}（commit {manifest.CommitReference}）｜" +
                              $"首个支持版本 {first.Tag}｜profile 主类 {profile.MainClass}｜库 {profile.Libraries.Count} 个");
        });

        await Case("authlib-injector 工件清单可解析", async () =>
        {
            var a = await http.GetJsonAsync<AuthlibInjectorArtifact>(AuthlibInjectorManager.OfficialArtifactUrl);
            Expect(a.BuildNumber > 0, "build_number 异常");
            Expect(!string.IsNullOrEmpty(a.Checksum?.Sha256), "缺 sha256");
            Console.WriteLine($"        v{a.Version} build {a.BuildNumber} → {a.DownloadUrl}");
        });

        await Case("真实下载一个历史版本的客户端 jar 并校验 sha1", async () =>
        {
            using var tmp = new TempDir();
            var paths = new Core.Common.GamePaths(tmp.Path);
            var flags = new Core.Config.FeatureFlags { GameDownloadEnabled = true, DownloadAcknowledgedAt = DateTime.Now };
            var installer = new VersionInstaller(http, paths, new MojangDownloadSource(), flags);

            var versions = await installer.ListVersionsAsync();
            var target = versions.First(v => v.Id == "1.0");
            var json = await installer.InstallAsync(target.Id, null, default, clientOnly: true);

            var jar = paths.VersionJar("1.0");
            Expect(File.Exists(jar), "客户端 jar 未落盘");
            Expect(new FileInfo(jar).Length > 100_000, "客户端 jar 小得异常");
            Expect(File.Exists(paths.VersionJson("1.0")), "版本 json 未落盘");
            Expect(json.MainClass.Length > 0, "版本 json 缺 mainClass");
            Console.WriteLine($"        {jar}（{new FileInfo(jar).Length / 1024} KB，sha1 校验通过）");
        });

        await Case("Fabric：某 MC 版本的 loader 列表与 profile json", async () =>
        {
            var m = await http.GetJsonAsync<VersionManifest>(MojangDownloadSource.ManifestV2);
            var game = m.Latest.Release;

            var loaders = await http.GetJsonAsync<List<FabricLoaderForVersion>>(ModLoaderApi.FabricLoadersFor(game));
            Expect(loaders.Count > 0, $"Fabric 对 {game} 没有可用 loader");

            var loader = loaders.First(l => l.Loader.Stable).Loader.Version;
            var profile = await http.GetJsonAsync<VersionJson>(ModLoaderApi.FabricProfile(game, loader));
            Expect(profile.MainClass.Contains("fabricmc"), "Fabric profile 主类异常：" + profile.MainClass);
            Expect(profile.InheritsFrom == game, "Fabric profile 的 inheritsFrom 与游戏版本不一致");
            Expect(profile.Libraries.Count > 0, "Fabric profile 没带 libraries");
            Console.WriteLine($"        MC {game}｜Fabric loader {loader}｜主类 {profile.MainClass}｜库 {profile.Libraries.Count} 个");
        });

        await Case("Fabric：BMCLAPI 镜像的 meta 也可用", async () =>
        {
            var mirrored = ModLoaderApi.FabricMirror(ModLoaderApi.FabricLoaderVersions);
            var loaders = await http.GetJsonAsync<List<FabricLoaderEntry>>(mirrored);
            Expect(loaders.Count > 0, "镜像返回的 loader 列表为空");
            Console.WriteLine($"        {mirrored}｜最新 loader {loaders[0].Version}");
        });

        await Case("Forge：maven-metadata 与 promotions_slim 可用", async () =>
        {
            var xml = await http.GetStringAsync(ModLoaderApi.ForgeMavenMetadataUrl);
            var all = ModLoaderApi.ParseMavenMetadataVersions(xml);
            Expect(all.Count > 100, "Forge 版本数量异常：" + all.Count);

            var promos = await http.GetJsonAsync<PromotionsSlim>(ModLoaderApi.ForgePromotionsUrl);
            var game = "1.20.1";
            var rec = ModLoaderApi.RecommendedVersion(promos, game);
            Expect(rec is not null, $"{game} 没有推荐版");

            var forGame = ModLoaderApi.FilterByGameVersion(all, game);
            Expect(forGame.Count > 0, $"{game} 在 maven 元数据里没有对应 Forge 版本");

            var url = ModLoaderApi.ForgeInstallerJar(rec!);
            using var head = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, default);
            Expect(head.IsSuccessStatusCode, "Forge 安装包地址不可达：" + url);
            var size = head.Content.Headers.ContentLength ?? 0;
            Console.WriteLine($"        Forge 共 {all.Count} 个版本｜{game} 有 {forGame.Count} 个｜推荐 {rec}（安装包 {size / 1024 / 1024} MB）");
        });

        await Case("OptiFine：BMCLAPI 镜像清单可用", async () =>
        {
            var entries = await http.GetJsonAsync<List<OptiFineEntry>>(OptiFineInstaller.ListUrl("1.20.1"));
            Expect(entries.Count > 0, "1.20.1 没有取到 OptiFine 条目");
            var first = entries[0];
            Expect(!string.IsNullOrEmpty(first.Patch), "条目缺 patch");
            Console.WriteLine($"        1.20.1 共 {entries.Count} 条｜首条 {first.Display}｜{first.Filename}");
        });

        await Case("Modrinth：搜索整合包与模组、真实解析一个 .mrpack", async () =>
        {
            var packs = await http.GetJsonAsync<MindCraftLauncher.Core.Mods.ModrinthSearchResponse>(
                MindCraftLauncher.Core.Mods.ModrinthApi.SearchUrl("", "modpack", "1.20.1", null, 5));
            Expect(packs.Hits.Count > 0, "整合包搜索没有结果");

            var mods = await http.GetJsonAsync<MindCraftLauncher.Core.Mods.ModrinthSearchResponse>(
                MindCraftLauncher.Core.Mods.ModrinthApi.SearchUrl("sodium", "mod", "1.20.1", "fabric", 5));
            Expect(mods.Hits.Count > 0, "模组搜索没有结果");

            var versions = await http.GetJsonAsync<List<MindCraftLauncher.Core.Mods.ModrinthVersion>>(
                MindCraftLauncher.Core.Mods.ModrinthApi.VersionsUrl(mods.Hits[0].ProjectId, "1.20.1", "fabric"));
            Expect(versions.Count > 0, "模组版本列表为空");
            Expect(versions[0].PrimaryFile is not null, "模组版本没有主文件");

            // 拉一个 mrpack 并解析索引（真实结构校验）
            var packVersions = await http.GetJsonAsync<List<MindCraftLauncher.Core.Mods.ModrinthVersion>>(
                MindCraftLauncher.Core.Mods.ModrinthApi.VersionsUrl(packs.Hits[0].ProjectId, null, null));
            var mrpack = packVersions.SelectMany(v => v.Files).FirstOrDefault(f => f.Filename.EndsWith(".mrpack"));
            Expect(mrpack is not null, "该整合包没有 .mrpack 文件");

            var bytes = await http.GetBytesAsync(mrpack!.Url);
            using var ms = new MemoryStream(bytes);
            using var zip = new System.IO.Compression.ZipArchive(ms);
            var entry = zip.GetEntry("modrinth.index.json");
            Expect(entry is not null, "mrpack 里没有 modrinth.index.json");
            using var st = entry!.Open();
            var idx = System.Text.Json.JsonSerializer.Deserialize<MindCraftLauncher.Core.Mods.ModpackIndex>(
                st, MindCraftLauncher.Core.Common.JsonOpt.Read);
            Expect(idx is not null && idx.MinecraftVersion.Length > 0, "索引缺 minecraft 依赖");
            Console.WriteLine($"        {packs.Hits[0].Title} {idx!.VersionId}｜MC {idx.MinecraftVersion}｜" +
                              $"Fabric {idx.FabricLoader ?? "-"}｜文件 {idx.ClientFiles.Count()} 个｜{bytes.Length / 1024} KB");
        });

        await Case("EasyTier 本机安装情况（未装只提示，不算失败）", async () =>
        {
            await Task.CompletedTask;
            var core = EasyTierRunner.Locate(null, GameLocation.LauncherBinaryRoot());
            if (core is null) { Console.WriteLine("        本机未安装 easytier-core（Linux 沙箱属正常）"); return; }
            Console.WriteLine("        找到：" + core);
        });

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "联网冒烟测试全部通过" : $"联网冒烟测试失败 {_failed} 项");
        return _failed;
    }

    private static async Task Case(string name, Func<Task> body)
    {
        Console.WriteLine("  LIVE  " + name);
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            // 镜像 / 外网不可达在容器里很常见，不算断言失败
            if (IsNetworkIssue(ex))
            {
                Console.WriteLine("        跳过（网络不可达）：" + ex.Message.Split('\n')[0]);
                return;
            }
            Console.WriteLine("        FAIL " + ex.Message.Split('\n')[0]);
            _failed++;
        }
    }

    private static int _failed;

    private static bool IsNetworkIssue(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException;

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new AssertException(message);
    }
}

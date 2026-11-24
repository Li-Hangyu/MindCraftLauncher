using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Launch;

namespace MindCraftLauncher.Core.Downloads;

/// <summary>安装进度。UI 直接绑定这些字段渲染进度条。</summary>
/// <param name="Phase">当前阶段：manifest / version / client / libraries / assets / done。</param>
/// <param name="Current">正在完成的项目数。</param>
/// <param name="Total">总项目数，未知为 -1。</param>
/// <param name="Label">给用户看的一行话。</param>
public readonly record struct InstallProgress(string Phase, int Current, int Total, string Label);

/// <summary>
/// 版本安装器：清单 → 版本 json → 客户端 jar → libraries → assets，全部带 sha1 校验、已存在跳过、并发限流。
/// 这一层是"PCL 内部逻辑"的等价最小实现；移植时可整体替换为 PCL 的下载模块，只要保持
/// <see cref="ListVersionsAsync"/> 与 <see cref="InstallAsync"/> 两个入口签名不变。
/// </summary>
public sealed class VersionInstaller
{
    private const int MaxParallel = 8;

    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly IDownloadSource _source;
    private readonly FeatureFlags _flags;

    public VersionInstaller(HttpFetcher http, GamePaths paths, IDownloadSource source, FeatureFlags flags)
    {
        _http = http;
        _paths = paths;
        _source = source;
        _flags = flags;
    }

    public IDownloadSource Source => _source;

    public async Task<VersionIntegrityReport> CheckIntegrityAsync(string versionId,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var json = VersionResolver.Resolve(_paths, versionId)
                   ?? throw new InvalidOperationException($"版本 {versionId} 的本地版本描述不存在或已损坏，无法检查文件完整性。");
        var files = new List<(string Path, string? Sha1, string Label)>();

        if (json.Downloads.Client is { } client)
            files.Add((_paths.VersionJar(json.Jar ?? json.Id), client.Sha1, "游戏客户端"));

        var osName = RuleEvaluator.CurrentOsName;
        foreach (var library in json.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, osName)))
        {
            var artifact = library.Downloads?.Artifact;
            if (artifact is not null)
                files.Add((Path.Combine(_paths.Libraries, artifact.Path ?? MavenPath.FromCoordinate(library.Name)), artifact.Sha1, library.Name));
            else if (!string.IsNullOrEmpty(library.Url) && !library.IsNativeOnly)
                files.Add((Path.Combine(_paths.Libraries, MavenPath.FromCoordinate(library.Name)), null, library.Name));

            var classifierName = GetNativeClassifierName(library, osName);
            if (classifierName is not null && library.Downloads?.Classifiers?.TryGetValue(classifierName, out var classifier) == true)
                files.Add((Path.Combine(_paths.Libraries, classifier.Path ?? MavenPath.FromCoordinate(library.Name, classifierName)), classifier.Sha1, library.Name + " native"));
        }

        List<KeyValuePair<string, string>> assetObjects = new();
        if (json.AssetIndex is { } assetIndex)
        {
            var indexPath = _paths.AssetIndex(assetIndex.Id ?? json.Assets);
            files.Add((indexPath, assetIndex.Sha1, "资源索引"));
            if (await IsValidFileAsync(indexPath, assetIndex.Sha1, ct).ConfigureAwait(false))
            {
                var indexText = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
                assetObjects = LoadAssetObjectsFromJson(indexText);
                files.AddRange(assetObjects.Select(item =>
                {
                    var hash = item.Value;
                    var path = Path.Combine(_paths.AssetsObjects, hash.Length >= 2 ? hash[..2] : "00", hash);
                    return (path, (string?)hash, "资源文件 " + item.Key);
                }));
            }
        }

        var invalid = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            if (!await IsValidFileAsync(file.Path, file.Sha1, ct).ConfigureAwait(false)) invalid.Add(file.Path);
            progress?.Report(new InstallProgress("verify", i + 1, files.Count,
                $"检查游戏文件 {i + 1}/{files.Count}：{file.Label}"));
        }

        return new VersionIntegrityReport(versionId, files.Count, invalid);
    }

    public async Task RepairAsync(string versionId, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();
        var json = VersionResolver.Resolve(_paths, versionId);
        if (json is null)
        {
            await InstallAsync(versionId, progress, ct).ConfigureAwait(false);
            return;
        }

        if (json.Downloads.Client is { } client)
        {
            progress?.Report(new InstallProgress("repair", 0, -1, "修复游戏客户端文件"));
            await _http.DownloadFileAsync(_source.RewriteUrl(client.Url),
                _paths.VersionJar(json.Jar ?? json.Id), client.Sha1, null, ct).ConfigureAwait(false);
        }

        await InstallLibrariesAsync(json, progress, ct).ConfigureAwait(false);
        await InstallNativeClassifiersAsync(json, progress, ct).ConfigureAwait(false);

        if (json.AssetIndex is { } index)
        {
            var indexPath = _paths.AssetIndex(index.Id ?? json.Assets);
            await _http.DownloadFileAsync(_source.RewriteUrl(index.Url), indexPath, index.Sha1, null, ct).ConfigureAwait(false);
            var objects = await LoadAssetObjectsAsync(indexPath, ct).ConfigureAwait(false);
            progress?.Report(new InstallProgress("assets", 0, objects.Count, $"修复游戏资源文件（{objects.Count} 个）"));
            await RunParallelAsync(objects, async (item, i, token) =>
            {
                var hash = item.Value;
                var prefix = hash.Length >= 2 ? hash[..2] : "00";
                var local = Path.Combine(_paths.AssetsObjects, prefix, hash);
                var url = $"https://resources.download.minecraft.net/{prefix}/{hash}";
                await _http.DownloadFileAsync(_source.RewriteUrl(url), local, hash, null, token).ConfigureAwait(false);
                if (i % 40 == 0)
                    progress?.Report(new InstallProgress("assets", i, objects.Count, $"资源文件 {i}/{objects.Count}"));
            }, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress("done", 1, 1, versionId + " 文件完整性修复完成"));
    }

    public async Task<IReadOnlyList<VersionManifest.VersionEntry>> ListVersionsAsync(CancellationToken ct = default)
    {
        var manifest = await _http.GetJsonAsync<VersionManifest>(_source.VersionManifestUrl, _source, ct).ConfigureAwait(false);
        return manifest.Versions;
    }

    /// <summary>下载并安装一个版本。返回安装后的版本 json。</summary>
    /// <param name="clientOnly">true 时只取版本描述与客户端 jar，不拉 libraries / assets，供连通性自检用。</param>
    public async Task<VersionJson> InstallAsync(string versionId,
        IProgress<InstallProgress>? progress, CancellationToken ct = default, bool clientOnly = false)
    {
        _flags.EnsureGameDownload();

        var manifest = await _http.GetJsonAsync<VersionManifest>(_source.VersionManifestUrl, _source, ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"清单中找不到版本 {versionId}");

        progress?.Report(new InstallProgress("version", 0, -1, "读取版本描述 " + versionId));
        var json = await LoadVersionJsonAsync(entry.Url, versionId, manifest, ct).ConfigureAwait(false);

        // 1) 客户端 jar
        if (json.Downloads.Client is { } client)
        {
            progress?.Report(new InstallProgress("client", 0, 1, "下载客户端 jar"));
            var target = _paths.VersionJar(versionId);
            await _http.DownloadFileAsync(_source.RewriteUrl(client.Url), target, client.Sha1, null, ct).ConfigureAwait(false);
        }

        // 落盘版本 json，启动器与 PCL 都以 versions/<id>/<id>.json 为准
        // 某些版本没有客户端文件，或客户端文件已存在时，前面的下载不会创建版本目录。
        Directory.CreateDirectory(_paths.VersionRoot(versionId));
        File.WriteAllText(_paths.VersionJson(versionId), JsonOpt.Serialize(json));

        if (clientOnly)
        {
            progress?.Report(new InstallProgress("done", 1, 1, versionId + " 客户端 jar 已就位（仅自检）"));
            return json;
        }

        // 2) libraries
        await InstallLibrariesAsync(json, progress, ct).ConfigureAwait(false);

        // 3) assets 索引 + 对象
        if (json.AssetIndex is { } index)
        {
            progress?.Report(new InstallProgress("assets", 0, -1, "下载资源索引 " + index.Id));
            var indexPath = _paths.AssetIndex(index.Id ?? json.Assets);
            await _http.DownloadFileAsync(_source.RewriteUrl(index.Url), indexPath, index.Sha1, null, ct).ConfigureAwait(false);

            var objects = await LoadAssetObjectsAsync(indexPath, ct).ConfigureAwait(false);
            progress?.Report(new InstallProgress("assets", 0, objects.Count, $"下载资源文件（{objects.Count} 个）"));
            await RunParallelAsync(objects, async (o, i, token) =>
            {
                var hash = o.Value;
                var local = Path.Combine(_paths.AssetsObjects, hash.Length >= 2 ? hash[..2] : "00", hash);
                var url = $"https://resources.download.minecraft.net/{(hash.Length >= 2 ? hash[..2] : "00")}/{hash}";
                await _http.DownloadFileAsync(_source.RewriteUrl(url), local, hash, null, token).ConfigureAwait(false);

                if (i % 40 == 0)
                    progress?.Report(new InstallProgress("assets", i, objects.Count, $"资源文件 {i}/{objects.Count}"));
            }, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress("done", 1, 1, versionId + " 安装完成"));
        return json;
    }

    /// <summary>
    /// 下载一份 version json 引用的全部 libraries。Fabric 安装器与 LabyMod 安装器都复用它，
    /// 避免各家实现里重复写一遍下载与校验。
    /// </summary>
    public async Task InstallLibrariesAsync(VersionJson json, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var libs = json.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, RuleEvaluator.CurrentOsName)).ToList();
        progress?.Report(new InstallProgress("libraries", 0, libs.Count, $"下载依赖库（{libs.Count} 个）"));

        await RunParallelAsync(libs, async (l, _, token) =>
        {
            var artifact = l.Downloads?.Artifact;
            if (artifact is not null)
            {
                var local = Path.Combine(_paths.Libraries, artifact.Path ?? MavenPath.FromCoordinate(l.Name));
                await _http.DownloadFileAsync(_source.RewriteUrl(artifact.Url), local, artifact.Sha1, null, token)
                    .ConfigureAwait(false);
                return;
            }

            // 只有 url 没有 downloads 的远古格式（Fabric profile 就是这种）
            if (!string.IsNullOrEmpty(l.Url) && !l.IsNativeOnly)
            {
                var relative = MavenPath.FromCoordinate(l.Name);
                var local = Path.Combine(_paths.Libraries, relative);
                await _http.DownloadFileAsync(_source.RewriteUrl(l.Url!.TrimEnd('/') + "/" + relative),
                    local, null, null, token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task InstallNativeClassifiersAsync(VersionJson json, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var osName = RuleEvaluator.CurrentOsName;
        var files = json.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, osName))
            .Select(l => (Library: l, Name: GetNativeClassifierName(l, osName)))
            .Where(item => item.Name is not null && item.Library.Downloads?.Classifiers?.ContainsKey(item.Name) == true)
            .ToList();
        progress?.Report(new InstallProgress("libraries", 0, files.Count, $"检查原生依赖（{files.Count} 个）"));
        await RunParallelAsync(files, async (item, _, token) =>
        {
            var classifier = item.Library.Downloads!.Classifiers![item.Name!];
            var relativePath = classifier.Path ?? MavenPath.FromCoordinate(item.Library.Name, item.Name);
            await _http.DownloadFileAsync(_source.RewriteUrl(classifier.Url),
                Path.Combine(_paths.Libraries, relativePath), classifier.Sha1, null, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static string? GetNativeClassifierName(LibraryRef library, string osName)
    {
        if (library.Natives is null) return null;
        var key = osName switch { "windows" => "windows", "osx" => "macos", _ => "linux" };
        if (!library.Natives.TryGetValue(key, out var value)) return null;
        return value.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
    }

    private static async Task<bool> IsValidFileAsync(string path, string? expectedSha1, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1)) return true;
        var actual = await Crypto.Sha1FileAsync(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 version json 落盘为 versions/&lt;id&gt;/&lt;id&gt;.json。</summary>
    public string WriteVersionJson(VersionJson json)
    {
        var path = _paths.VersionJson(json.Id);
        Directory.CreateDirectory(_paths.VersionRoot(json.Id));
        File.WriteAllText(path, JsonOpt.Serialize(json));
        return path;
    }

    /// <summary>解析 inheritsFrom：Forge / LabyMod 的 profile 只写差异，其余继承父版本。</summary>
    private async Task<VersionJson> LoadVersionJsonAsync(string url, string versionId,
        VersionManifest manifest, CancellationToken ct)
    {
        var json = await _http.GetJsonAsync<VersionJson>(url, _source, ct).ConfigureAwait(false);

        var depth = 0;
        while (!string.IsNullOrEmpty(json.InheritsFrom) && depth++ < 4)
        {
            var parentId = json.InheritsFrom!;
            var child = json;

            VersionJson parent;
            var localParent = _paths.VersionJson(parentId);
            if (File.Exists(localParent))
            {
                parent = JsonOpt.Deserialize<VersionJson>(File.ReadAllText(localParent))
                         ?? throw new InvalidOperationException("父版本 json 解析失败：" + parentId);
            }
            else
            {
                var pe = manifest.Versions.FirstOrDefault(v => v.Id == parentId)
                         ?? throw new InvalidOperationException($"需要先安装父版本 {parentId}");
                parent = await _http.GetJsonAsync<VersionJson>(pe.Url, _source, ct).ConfigureAwait(false);
            }

            json = Merge(parent, child);
        }
        return json;
    }

    /// <summary>子版本覆盖父版本的标量字段，libraries 与 arguments 追加。</summary>
    internal static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var merged = JsonOpt.Deserialize<VersionJson>(JsonOpt.Serialize(parent))!;
        merged.Id = child.Id;
        merged.Type = string.IsNullOrEmpty(child.Type) ? parent.Type : child.Type;
        if (!string.IsNullOrEmpty(child.MainClass)) merged.MainClass = child.MainClass;
        if (!string.IsNullOrEmpty(child.Assets)) merged.Assets = child.Assets;
        if (!string.IsNullOrEmpty(child.MinecraftArguments)) merged.MinecraftArguments = child.MinecraftArguments;
        if (child.JarMod is not null) merged.JarMod = child.JarMod;
        if (child.Jar is not null) merged.Jar = child.Jar;
        if (child.Downloads.Client is not null) merged.Downloads = child.Downloads;
        if (child.AssetIndex is not null) merged.AssetIndex = child.AssetIndex;
        if (child.JavaVersion.MajorVersion != 8 || string.IsNullOrEmpty(merged.JavaVersion.Component))
            merged.JavaVersion = child.JavaVersion;

        merged.Libraries = parent.Libraries.Concat(child.Libraries).ToList();
        merged.InheritsFrom = null;

        if (child.Arguments is not null)
        {
            merged.Arguments ??= new VersionJson.ArgumentsBag();
            merged.Arguments.Game = (parent.Arguments?.Game ?? new()).Concat(child.Arguments.Game ?? new()).ToList();
            merged.Arguments.Jvm = (parent.Arguments?.Jvm ?? new()).Concat(child.Arguments.Jvm ?? new()).ToList();
        }
        return merged;
    }

    internal static List<KeyValuePair<string, string>> LoadAssetObjectsFromJson(string jsonText)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
        var list = new List<KeyValuePair<string, string>>();
        if (doc.RootElement.TryGetProperty("objects", out var objects) && objects.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in objects.EnumerateObject())
                if (p.Value.TryGetProperty("hash", out var h))
                    list.Add(new KeyValuePair<string, string>(p.Name, h.GetString() ?? ""));
        }
        return list.Where(x => !string.IsNullOrEmpty(x.Value)).ToList();
    }

    private async Task<List<KeyValuePair<string, string>>> LoadAssetObjectsAsync(string indexPath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
        return LoadAssetObjectsFromJson(text);
    }

    private static async Task RunParallelAsync<T>(IReadOnlyList<T> items,
        Func<T, int, CancellationToken, Task> body, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = new List<Task>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var capture = (Item: items[i], Index: i);
            tasks.Add(Task.Run(async () =>
            {
                try { await body(capture.Item, capture.Index, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public sealed record VersionIntegrityReport(string VersionId, int CheckedFiles, IReadOnlyList<string> InvalidFiles)
{
    public bool IsHealthy => InvalidFiles.Count == 0;
}

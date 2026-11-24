using System.Diagnostics;
using System.Windows;

namespace MindCraftLauncher.App;

/// <summary>
/// 主题切换：App.xaml 里合并了 Palette / 主题 / Controls 三个字典，
/// 切主题只替换其中「主题」那一本，键名保持一致，所有控件靠 DynamicResource 自动重绘。
///
/// 修掉的问题：
/// 1. 原来用相对 URI（"Themes/Dark.xaml"）在运行时构造 ResourceDictionary，取决于
///    base URI 的解析结果，某些情况下会抛 "无法找到资源"，把异常抛到 UI 线程直接把程序打崩；
///    现在优先用 pack URI，失败再退到相对 URI，全部包在 try/catch 里。
/// 2. 目标主题已经是当前主题时直接返回，避免每次设置页保存都整树重建、界面闪一下。
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";

    private const string DarkFile = "Dark.xaml";
    private const string LightFile = "Light.xaml";

    /// <summary>把任意输入收敛成 "dark" / "light"。</summary>
    public static string Normalize(string? theme) =>
        string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static bool IsLight(string? theme) => Normalize(theme) == Light;

    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var file = IsLight(theme) ? LightFile : DarkFile;
        var dicts = app.Resources.MergedDictionaries;

        // 已经是目标主题：不做任何事
        foreach (var d in dicts)
        {
            var src = d.Source?.OriginalString ?? "";
            if (src.EndsWith(file, StringComparison.OrdinalIgnoreCase)) return;
        }

        var replacement = Load(file);
        if (replacement is null) return;   // 加载失败就保持当前主题，绝不把异常抛到 UI 线程

        // 原位替换，避免 Controls.xaml 里的 DynamicResource 短暂找不到键
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.EndsWith(DarkFile, StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith(LightFile, StringComparison.OrdinalIgnoreCase))
            {
                dicts[i] = replacement;
                return;
            }
        }

        dicts.Add(replacement);
    }

    private static ResourceDictionary? Load(string file)
    {
        // 1) pack URI：最可靠，与程序集名绑定
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{AppInfo.AssemblyName};component/Themes/{file}", UriKind.Absolute),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[ThemeManager] pack URI 加载失败：" + ex.Message);
        }

        // 2) 退路：相对 URI
        try
        {
            return new ResourceDictionary { Source = new Uri("Themes/" + file, UriKind.Relative) };
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[ThemeManager] 相对 URI 加载失败：" + ex.Message);
            return null;
        }
    }
}

/// <summary>程序集级常量。pack URI 依赖程序集名，集中在一处，改名不用满仓库找。</summary>
public static class AppInfo
{
    public const string AssemblyName = "MindCraftLauncher";
    public const string DisplayName = "Mind Craft Launcher";
}

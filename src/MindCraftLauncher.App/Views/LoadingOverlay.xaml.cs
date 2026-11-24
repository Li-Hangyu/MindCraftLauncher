using System.Windows;
using System.Windows.Controls;

namespace MindCraftLauncher.App.Views;

public partial class LoadingOverlay : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("请稍候…"));
    public LoadingOverlay() => InitializeComponent();

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}

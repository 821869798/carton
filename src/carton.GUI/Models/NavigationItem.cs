using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace carton.GUI.Models;

public sealed partial class NavigationItem : ObservableObject
{
    public NavigationItem(NavigationPage page, string titleResourceKey, string fallbackTitle, string iconData)
    {
        Page = page;
        TitleResourceKey = titleResourceKey;
        FallbackTitle = fallbackTitle;
        _title = fallbackTitle;
        IconGeometry = Geometry.Parse(iconData);
    }

    public NavigationPage Page { get; }

    public string TitleResourceKey { get; }

    public string FallbackTitle { get; }

    public Geometry IconGeometry { get; }

    [ObservableProperty]
    private string _title;
}

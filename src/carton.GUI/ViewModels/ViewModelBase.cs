using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
}

public abstract partial class PageViewModelBase : ViewModelBase
{
    private string _titleResourceKey = string.Empty;
    private string _titleFallback = string.Empty;
    private bool _isLocalizedTitleInitialized;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    public abstract NavigationPage PageType { get; }

    protected void InitializePageMetadata(string icon, string titleResourceKey, string titleFallback)
    {
        Icon = icon;
        _titleResourceKey = titleResourceKey;
        _titleFallback = titleFallback;

        if (!_isLocalizedTitleInitialized)
        {
            LocalizationService.Instance.LanguageChanged += OnPageLanguageChanged;
            _isLocalizedTitleInitialized = true;
        }

        UpdateLocalizedTitle();
    }

    protected void UpdateLocalizedTitle()
    {
        if (string.IsNullOrWhiteSpace(_titleResourceKey))
        {
            return;
        }

        var value = LocalizationService.Instance.GetString(_titleResourceKey);
        Title = string.Equals(value, _titleResourceKey, StringComparison.Ordinal)
            ? _titleFallback
            : value;
    }

    private void OnPageLanguageChanged(object? sender, carton.Core.Models.AppLanguage language)
    {
        UpdateLocalizedTitle();
    }
}

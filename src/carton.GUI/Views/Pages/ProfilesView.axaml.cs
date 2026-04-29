using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using carton.Core.Services;
using carton.GUI.Controls;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class ProfilesView : UserControl
{
    private static readonly IBrush SearchSelectedBrush =
        new SolidColorBrush(Color.FromArgb(80, 96, 160, 255));

    private readonly IPreferencesService _preferencesService;
    private ProfilesViewModel? _subscribedViewModel;
    private bool _wasSearchOpen;
    private bool _wasEditorVisible;
    private double _fontSizeOnEditorOpen;

    public ProfilesView()
    {
        InitializeComponent();
        _preferencesService = App.PreferencesService;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        ConfigEditor.EditorStateChanged += OnEditorStateChanged;
        ConfigSearchInput.TextChanged += OnSearchTextChanged;
        ConfigSearchInput.KeyDown += OnSearchInputKeyDown;

        ToolTip.SetTip(ConfigSearchCaseButton, "区分大小写");
        ToolTip.SetTip(ConfigSearchWholeWordButton, "全字匹配");
        ToolTip.SetTip(ConfigSearchRegexButton, "正则表达式");
        ToolTip.SetTip(ConfigSearchPreviousButton, "上一处");
        ToolTip.SetTip(ConfigSearchNextButton, "下一处");
        ToolTip.SetTip(ConfigSearchCloseButton, "关闭搜索");

        LoadEditorFontSizePreference();
        UpdateEditorActionState();
    }

    private ProfilesViewModel? ViewModel => DataContext as ProfilesViewModel;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SubscribeViewModel(ViewModel);
        UpdateEditorActionState();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_wasEditorVisible)
        {
            SaveEditorFontSizeIfChanged();
        }

        _wasEditorVisible = false;
        UnsubscribeViewModel(_subscribedViewModel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeViewModel(_subscribedViewModel);
        SubscribeViewModel(ViewModel);
        UpdateEditorActionState();
    }

    private void SubscribeViewModel(ProfilesViewModel? viewModel)
    {
        if (viewModel == null)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribedViewModel = viewModel;
    }

    private void UnsubscribeViewModel(ProfilesViewModel? viewModel)
    {
        if (viewModel == null)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfilesViewModel.ShowConfigFullscreenView))
        {
            UpdateEditorActionState();
        }
    }

    private void OnEditorStateChanged(object? sender, EventArgs e)
    {
        UpdateEditorActionState();
    }

    private void UpdateEditorActionState()
    {
        var editorVisible = ViewModel?.ShowConfigFullscreenView == true;
        var isSearchOpen = editorVisible && ConfigEditor.IsSearchOpen;

        if (editorVisible && !_wasEditorVisible)
        {
            _fontSizeOnEditorOpen = ConfigEditor.EditorFontSize;
        }
        else if (!editorVisible && _wasEditorVisible)
        {
            SaveEditorFontSizeIfChanged();
        }

        SearchConfigButton.IsEnabled = editorVisible;
        ConfigSearchBar.IsVisible = isSearchOpen;
        ConfigSearchStatus.Text = ConfigEditor.SearchStatusText;
        ConfigSearchPreviousButton.IsEnabled = ConfigEditor.HasSearchMatches;
        ConfigSearchNextButton.IsEnabled = ConfigEditor.HasSearchMatches;
        ConfigSearchCaseButton.Background = ConfigEditor.SearchCaseSensitive ? SearchSelectedBrush : null;
        ConfigSearchWholeWordButton.Background = ConfigEditor.SearchWholeWord ? SearchSelectedBrush : null;
        ConfigSearchRegexButton.Background = ConfigEditor.SearchUseRegex ? SearchSelectedBrush : null;

        if (isSearchOpen && !_wasSearchOpen)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ConfigSearchInput.Focus();
                ConfigSearchInput.SelectAll();
            });
        }

        _wasSearchOpen = isSearchOpen;
        _wasEditorVisible = editorVisible;
    }

    private void LoadEditorFontSizePreference()
    {
        var preferences = _preferencesService.Load();
        var fontSize = preferences.JsonEditorFontSize > 0
            ? preferences.JsonEditorFontSize
            : JsonConfigEditor.DefaultEditorFontSize;
        ConfigEditor.EditorFontSize = fontSize;
    }

    private void SaveEditorFontSizeIfChanged()
    {
        var currentFontSize = ConfigEditor.EditorFontSize;
        if (Math.Abs(currentFontSize - _fontSizeOnEditorOpen) < 0.01)
        {
            return;
        }

        var preferences = _preferencesService.Load();
        if (Math.Abs(preferences.JsonEditorFontSize - currentFontSize) < 0.01)
        {
            return;
        }

        preferences.JsonEditorFontSize = currentFontSize;
        _preferencesService.Save(preferences);
    }

    private void OnSearchConfigClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.OpenSearch();
        UpdateEditorActionState();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ConfigEditor.SetSearchQuery(ConfigSearchInput.Text ?? string.Empty);
        UpdateEditorActionState();
    }

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                ConfigEditor.FindPrevious();
            }
            else
            {
                ConfigEditor.FindNext();
            }

            UpdateEditorActionState();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnSearchCloseClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnSearchCaseClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.ToggleCaseSensitive();
        UpdateEditorActionState();
    }

    private void OnSearchWholeWordClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.ToggleWholeWord();
        UpdateEditorActionState();
    }

    private void OnSearchRegexClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.ToggleRegex();
        UpdateEditorActionState();
    }

    private void OnSearchPreviousClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.FindPrevious();
        UpdateEditorActionState();
    }

    private void OnSearchNextClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.FindNext();
        UpdateEditorActionState();
    }

    private void OnSearchCloseClick(object? sender, RoutedEventArgs e)
    {
        ConfigEditor.CloseSearch();
        ConfigSearchInput.Text = string.Empty;
        ConfigSearchBar.IsVisible = false;
        UpdateEditorActionState();
    }
}

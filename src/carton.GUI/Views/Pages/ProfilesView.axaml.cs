using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using carton.ViewModels;
using carton.GUI.Controls;

namespace carton.Views.Pages;

public partial class ProfilesView : UserControl, IDisposable
{
    private JsonConfigEditor? _configEditor;
    private bool _isDisposed;

    public ProfilesView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private ProfilesViewModel? ViewModel => DataContext as ProfilesViewModel;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SubscribeViewModel(ViewModel);
        SyncConfigEditorLifetime();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DisposeConfigEditor();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeViewModel(ViewModel);
        SyncConfigEditorLifetime();
    }

    private void SubscribeViewModel(ProfilesViewModel? viewModel)
    {
        if (viewModel == null)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfilesViewModel.ShowConfigFullscreenView))
        {
            SyncConfigEditorLifetime();
        }
    }

    private void SyncConfigEditorLifetime()
    {
        if (ViewModel?.ShowConfigFullscreenView == true)
        {
            EnsureConfigEditor();
        }
        else
        {
            DisposeConfigEditor();
        }
    }

    private void EnsureConfigEditor()
    {
        if (_configEditor != null || ViewModel == null)
        {
            return;
        }

        _configEditor = new JsonConfigEditor
        {
            [!JsonConfigEditor.TextProperty] = new Binding(nameof(ProfilesViewModel.ConfigContent), BindingMode.TwoWay),
            [!JsonConfigEditor.IsReadOnlyProperty] = new Binding(nameof(ProfilesViewModel.IsConfigReadOnly))
        };
        _configEditor.DataContext = ViewModel;
        LazyConfigEditorHost.Content = _configEditor;
    }

    private void DisposeConfigEditor()
    {
        if (_configEditor == null)
        {
            return;
        }

        LazyConfigEditorHost.Content = null;
        _configEditor = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        DataContextChanged -= OnDataContextChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        DisposeConfigEditor();
    }
}

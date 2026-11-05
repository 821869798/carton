using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public partial class SettingsViewModel
{
    private bool _materialSavePending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMaterialSaveError))]
    private string? _materialSaveError;
    public bool HasMaterialSaveError => !string.IsNullOrEmpty(MaterialSaveError);

    [ObservableProperty]
    private int _selectedWindowMaterial;
    public bool IsMaterialFallback => WindowMaterialService.Instance.IsFallback;

    partial void OnSelectedWindowMaterialChanged(int value)
    {
        if (_suppressPreferenceUpdates) return;
        _currentPreferences.WindowMaterial = (WindowMaterial)value;
        WindowMaterialService.Instance.Apply(_currentPreferences);
        _materialSavePending = true;
        SaveWindowMaterial();
    }

    private void LoadMaterialPreferences()
    {
        SelectedWindowMaterial = (int)_currentPreferences.WindowMaterial;
    }

    [RelayCommand]
    private void SaveWindowMaterial()
    {
        if (!_materialSavePending) return;
        try
        {
            PersistPreferences();
            _materialSavePending = false;
            MaterialSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MaterialSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[WindowMaterial] Preferences save failed: {ex}");
        }
    }

    private void DisposeMaterialSettings() => SaveWindowMaterial();

    private void OnMaterialAvailabilityChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(IsMaterialFallback));

    [RelayCommand]
    private void ResetWindowMaterial()
    {
        SelectedWindowMaterial = 0;
        SaveWindowMaterial();
    }
}

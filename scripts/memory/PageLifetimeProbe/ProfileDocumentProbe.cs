using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Threading;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Controls;
using carton.GUI.Services;
using carton.ViewModels;

internal static class ProfileDocumentProbe
{
    internal static int Run()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithLifetime(lifetime);
        LocalizationService.Instance.Initialize(AppLanguage.English);
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var directory = Path.Combine(Path.GetTempPath(), "carton-document-" + Guid.NewGuid());
        Dispatcher.UIThread.Post(async () =>
        {
            ProfilesViewModel? vm = null;
            Window? window = null;
            try
            {
                var configs = new ConfigManager(directory);
                var profiles = new ProfileManager(directory, configs);
                var profile = await profiles.CreateAsync(new Profile { Name = "Document probe", Type = ProfileType.Local }, "{\"a\":1}");
                vm = new ProfilesViewModel();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(ProfilesViewModel).GetField("_profileManager", flags)!.SetValue(vm, profiles);
                typeof(ProfilesViewModel).GetField("_configManager", flags)!.SetValue(vm, configs);
                await vm.RefreshAsync();
                await vm.EditProfileCommand.ExecuteAsync(vm.Profiles.Single());
                // Real content-loading path and its initial/saved baseline.
                await (Task)typeof(ProfilesViewModel).GetMethod("LoadConfigContentForEditorAsync", flags)!.Invoke(vm, null)!;
                vm.IsContentEditorVisible = true;
                var editor = new JsonConfigEditor { Document = vm.ConfigDocument };
                window = new Window { Content = editor, Width = 700, Height = 500 }; window.Show();
                var surface = typeof(JsonConfigEditor).GetField("_surface", flags)!.GetValue(editor)!;
                var select = surface.GetType().GetMethod("SelectRange")!;
                var insert = surface.GetType().GetMethod("InsertText", flags)!;
                select.Invoke(surface, new object[] { 5, 1 });
                insert.Invoke(surface, new object[] { "2" });
                if (!vm.CanSaveConfigInEditor || vm.ConfigContent != "{\"a\":2}") throw new Exception("Document binding or dirty state stale");
                editor.Undo();
                if (vm.CanSaveConfigInEditor) throw new Exception("Undo to baseline is still dirty");
                editor.Redo();
                await vm.SaveProfileInEditorCommand.ExecuteAsync(null);
                if (await configs.LoadConfigAsync(profile.Id, ProfileType.Local) != "{\"a\":2}" || vm.CanSaveConfigInEditor)
                    throw new Exception("Save did not persist latest buffer or mark clean");
                editor.Undo();
                if (!vm.CanSaveConfigInEditor || vm.ConfigContent != "{\"a\":1}") throw new Exception("Undo after save lost saved boundary");
                editor.Redo();
                if (vm.CanSaveConfigInEditor) throw new Exception("Redo to saved contents not clean");
                vm.FormatConfigJsonCommand.Execute(null);
                if (!vm.ConfigDocument.IsModified || !vm.ConfigContent.Contains('\n') || editor.Text != vm.ConfigContent)
                    throw new Exception("Formatting did not update document/editor");
                await vm.ValidateConfigJsonCommand.ExecuteAsync(null);
                await vm.SaveProfileInEditorCommand.ExecuteAsync(null);
                if (await configs.LoadConfigAsync(profile.Id, ProfileType.Local) != vm.ConfigContent) throw new Exception("Formatted save stale");
                var savedContent = vm.ConfigContent;
                lifetime.MainWindow = window;
                vm.ConfigDocument.Replace(vm.ConfigDocument.Length, 0, " ");
                async Task ChooseOnBack(string label)
                {
                    var navigation = vm.GoBackCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();
                    var dialog = window.OwnedWindows.Single();
                    var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await navigation;
                }
                await ChooseOnBack("Cancel");
                if (!vm.ShowConfigFullscreenView || !vm.ConfigDocument.IsModified) throw new Exception("Cancel discarded edits");
                await ChooseOnBack("Don't Save");
                if (vm.ShowConfigFullscreenView || vm.ConfigDocument.Length != 0 || await configs.LoadConfigAsync(profile.Id, ProfileType.Local) != savedContent)
                    throw new Exception("Discard changed disk or retained editing contents");
                await (Task)typeof(ProfilesViewModel).GetMethod("LoadConfigContentForEditorAsync", flags)!.Invoke(vm, null)!;
                vm.IsContentEditorVisible = true;
                if (editor.Text != savedContent || vm.ConfigDocument.IsModified) throw new Exception("Reopen after discard is stale");
                vm.ConfigDocument.Replace(vm.ConfigDocument.Length, 0, "  ");
                await ChooseOnBack("Save");
                if (vm.ShowConfigFullscreenView || vm.ConfigDocument.Length != 0 || await configs.LoadConfigAsync(profile.Id, ProfileType.Local) != savedContent + "  ")
                    throw new Exception("Save-on-exit did not persist buffer");
                Console.WriteLine("PASS real profile document binding, clean/dirty undo/redo, disk save, format, validate, cancel/discard/save dialogs and reopen");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); vm?.Dispose(); stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (failure != null) throw failure;
        return 0;
    }
}

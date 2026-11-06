using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using carton.Core.Models;
using carton.GUI.Services;
using carton.ViewModels;
using carton.Views.Pages;

// Counts actual realized controls and managed allocations, not GPU memory or AOT working set.
internal static class ProfileListProbe
{
    internal static int Run(bool verify)
    {
        var directory = AppContext.BaseDirectory;
        File.WriteAllText(Path.Combine(directory, ".carton_portable_data"), "");
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        File.WriteAllText(Path.Combine(directory, "data/preferences.json"),
            "{\"autoStartOnLaunch\":false,\"autoCheckAppUpdates\":false,\"language\":0,\"theme\":1,\"windowMaterial\":0}");
        AppBuilder.Configure<carton.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        Measure(8); // Warm framework styles and compiled bindings before comparing list sizes.
        var passed = true;
        foreach (var count in new[] { 0, 8, 100, 500 })
        {
            Collect();
            var result = Measure(count);
            Collect();
            var retainedImmediately = result.View.IsAlive;
            // Let pending UI/animation callbacks retire before asserting a permanent root.
            using (var settled = new CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
                Dispatcher.UIThread.MainLoop(settled.Token);
            Collect();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                profiles = count,
                result.RealizedCards,
                result.Controls,
                result.AllocatedBytes,
                result.LastRowReachable,
                result.EditCommandBound,
                interactionChecksPassed = true,
                viewRetainedImmediately = retainedImmediately,
                viewRetained = result.View.IsAlive,
                viewModelRetained = result.ViewModel.IsAlive
            }));
            passed &= result.LastRowReachable && result.EditCommandBound && !result.View.IsAlive && !result.ViewModel.IsAlive;
            if (count > 20) passed &= result.RealizedCards <= 20;
        }
        return verify && !passed ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement Measure(int count)
    {
        var viewModel = new ProfilesViewModel();
        foreach (var index in Enumerable.Range(1, count))
        {
            viewModel.Profiles.Add(new ProfileItemViewModel
            {
                Id = index, Name = $"Test profile {index}", Type = "Local", LastUpdated = "Never", CanDelete = true
            });
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var view = new ProfilesView { DataContext = viewModel };
        var window = new Window { Width = 780, Height = 590, Content = view };
        window.Show();
        Layout(window);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var controls = view.GetVisualDescendants().Count();
        var realizedCards = Cards(view).Count();
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Layout(window);
        var last = Cards(view).FirstOrDefault(card => card.DataContext is ProfileItemViewModel item && item.Id == count);
        var edit = last?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("profileEditArea"));
        var result = new Measurement(realizedCards, controls, allocated, count == 0 || last != null,
            count == 0 || edit?.Command == viewModel.EditProfileCommand && ReferenceEquals(edit?.CommandParameter, viewModel.Profiles[^1]),
            new WeakReference(view), new WeakReference(viewModel));
        CheckInteractions(view, viewModel, window, scroll);
        window.Close();
        window.Content = null;
        view.DataContext = null;
        viewModel.Dispose();
        return result;
    }

    private static void CheckInteractions(ProfilesView view, ProfilesViewModel model, Window window, ScrollViewer scroll)
    {
        // Exercise recycling in both directions, without adding this work to construction allocation.
        if (model.Profiles.Count > 8)
        {
            scroll.Offset = default;
            Layout(window);
            var boundaryCard = Cards(view).Take(8).Last();
            var boundary = (ProfileItemViewModel)boundaryCard.DataContext!;
            var lastButton = boundaryCard.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, model.DeleteProfileCommand));
            window.Activate();
            if (!lastButton.Focus()) throw new InvalidOperationException("Could not focus a profile action.");
            Layout(window);
            if (!ReferenceEquals(window.FocusManager?.GetFocusedElement(), lastButton))
                throw new InvalidOperationException("Profile action lost focus before Tab was sent.");
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Layout(window);
            var focusedCard = (window.FocusManager?.GetFocusedElement() as Visual)?.GetVisualAncestors()
                .OfType<Border>().FirstOrDefault(border => border.Classes.Contains("profileCard"));
            if (focusedCard?.DataContext is not ProfileItemViewModel next || next.Id != boundary.Id + 1)
            {
                var panel = view.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
                var owner = view.GetVisualDescendants().OfType<ItemsControl>().First(control => ReferenceEquals(control.ItemsSource, model.Profiles));
                var focus = window.FocusManager?.GetFocusedElement() as Control;
                throw new InvalidOperationException($"Tab expected profile {boundary.Id + 1}, actual {(focusedCard?.DataContext as ProfileItemViewModel)?.Id}; focus {focus?.GetType().Name}; panel TabNavigation={panel?.GetValue(KeyboardNavigation.TabNavigationProperty)}, owner={owner.GetValue(KeyboardNavigation.TabNavigationProperty)}.");
            }
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            Layout(window);
            if (window.FocusManager?.GetFocusedElement() is not Button previous ||
                !ReferenceEquals(previous.DataContext, boundary) || !ReferenceEquals(previous.Command, model.DeleteProfileCommand))
                throw new InvalidOperationException("Shift+Tab did not return to the previous profile action.");

            // Traverse several viewports in both directions with normal keyboard focus,
            // including the edge at which a target container has not been realized yet.
            scroll.Offset = default;
            Layout(window);
            var firstEdit = Cards(view).First(card => ReferenceEquals(card.DataContext, model.Profiles[0]))
                .GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, model.EditProfileCommand));
            firstEdit.Focus(NavigationMethod.Tab);
            Layout(window);
            var steps = Math.Min(24, (model.Profiles.Count - 1) * 2);
            for (var step = 1; step <= steps; step++)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Layout(window);
                CheckKeyboardFocus(window, model, step);
            }
            for (var step = steps - 1; step >= 0; step--)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
                Layout(window);
                CheckKeyboardFocus(window, model, step);
            }
        }
        if (model.Profiles.Count > 0)
        {
            var first = model.Profiles[0];
            var last = model.Profiles[^1];
            last.CanDelete = false;
            last.IsRemoteType = true;
            for (var pass = 0; pass < 3; pass++)
            {
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Layout(window);
                CheckRow(view, model, last);
                scroll.Offset = default;
                Layout(window);
                CheckRow(view, model, first);
            }
            window.Width = 580;
            window.Height = 430;
            ThemeService.Instance.ApplyTheme(AppTheme.Dark);
            LocalizationService.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Layout(window);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Layout(window);
            CheckRow(view, model, last);
            ThemeService.Instance.ApplyTheme(AppTheme.Light);
            LocalizationService.Instance.SetLanguage(AppLanguage.English);
            model.Profiles.Remove(last);
            Layout(window);
            if (Cards(view).Any(card => ReferenceEquals(card.DataContext, last)))
                throw new InvalidOperationException("Removed profile is still realized.");
        }
        model.Profiles.Clear();
        Layout(window);
        if (Cards(view).Any()) throw new InvalidOperationException("Cleared list still shows cards.");
        var added = new ProfileItemViewModel { Id = 1234, Name = "Repopulated profile", Type = "Local", CanDelete = true };
        model.Profiles.Add(added);
        scroll.Offset = default;
        Layout(window);
        CheckRow(view, model, added);
    }

    private static void CheckKeyboardFocus(Window window, ProfilesViewModel model, int step)
    {
        if (window.FocusManager?.GetFocusedElement() is not Button button ||
            !ReferenceEquals(button.DataContext, model.Profiles[step / 2]) ||
            !ReferenceEquals(button.Command, step % 2 == 0 ? model.EditProfileCommand : model.DeleteProfileCommand))
            throw new InvalidOperationException($"Unexpected profile keyboard focus at traversal step {step}.");
    }

    private static void CheckRow(ProfilesView view, ProfilesViewModel model, ProfileItemViewModel item)
    {
        var card = Cards(view).SingleOrDefault(card => ReferenceEquals(card.DataContext, item))
            ?? throw new InvalidOperationException($"Profile {item.Id} is unreachable after scrolling/reloading.");
        var buttons = card.GetVisualDescendants().OfType<Button>().ToArray();
        var edit = buttons.Single(button => ReferenceEquals(button.Command, model.EditProfileCommand));
        var update = buttons.Single(button => ReferenceEquals(button.Command, model.UpdateProfileCommand));
        var delete = buttons.Single(button => ReferenceEquals(button.Command, model.DeleteProfileCommand));
        if (!ReferenceEquals(edit.CommandParameter, item) || !ReferenceEquals(update.CommandParameter, item) ||
            !ReferenceEquals(delete.CommandParameter, item) || delete.IsEnabled != item.CanDelete || update.IsVisible != item.IsRemoteType)
            throw new InvalidOperationException($"Recycled profile {item.Id} has stale action bindings.");
    }

    private static IEnumerable<Border> Cards(ProfilesView view) =>
        view.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("profileCard"));

    private static void Layout(Window window)
    {
        for (var frame = 0; frame < 3; frame++)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Collect()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record Measurement(int RealizedCards, int Controls, long AllocatedBytes,
        bool LastRowReachable, bool EditCommandBound, WeakReference View, WeakReference ViewModel);
}

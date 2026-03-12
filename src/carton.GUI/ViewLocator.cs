using Avalonia.Controls;
using Avalonia.Controls.Templates;
using carton.ViewModels;
using carton.Views.Pages;

namespace carton;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        return data switch
        {
            DashboardViewModel => new DashboardView(),
            ProfilesViewModel => new ProfilesView(),
            GroupsViewModel => new GroupsView(),
            ConnectionsViewModel => new ConnectionsView(),
            LogsViewModel => new LogsView(),
            SettingsViewModel => new SettingsView(),
            _ => new TextBlock { Text = $"Not Found: {data.GetType().Name}" }
        };
    }

    public bool Match(object? data)
    {
        return data is PageViewModelBase;
    }
}

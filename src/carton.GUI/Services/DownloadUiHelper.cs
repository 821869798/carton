using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using carton.Core.Utilities;

namespace carton.GUI.Services;

public static class DownloadUiHelper
{
    public static string FormatStatus(
        string status,
        long bytesReceived,
        long totalBytes,
        string unknownLabel)
    {
        return totalBytes > 0 || bytesReceived > 0
            ? $"{status} {FormatHelper.FormatByteProgress(bytesReceived, totalBytes, unknownLabel)}"
            : status;
    }

    public static async Task<bool> ShowRetryDialogAsync(
        Window owner,
        string title,
        string messageFormat,
        string retryButtonText,
        string cancelButtonText,
        string detail,
        string unknownLabel)
    {
        var dialog = new Window
        {
            Width = 480,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title
        };

        var message = new TextBlock
        {
            Text = string.Format(messageFormat, string.IsNullOrWhiteSpace(detail) ? unknownLabel : detail),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var retryButton = new Button
        {
            Content = retryButtonText,
            MinWidth = 90
        };
        retryButton.Click += (_, _) => dialog.Close(true);

        var cancelButton = new Button
        {
            Content = cancelButtonText,
            MinWidth = 90
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        retryButton
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }
}

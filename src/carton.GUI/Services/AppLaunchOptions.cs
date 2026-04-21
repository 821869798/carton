using System;
using System.Linq;

namespace carton.GUI.Services;

public sealed class AppLaunchOptions
{
    public const string BackgroundArgument = "--background";

    public static AppLaunchOptions Default { get; } = new();

    public bool StartHidden { get; init; }

    public static AppLaunchOptions Parse(string[] args)
    {
        return new AppLaunchOptions
        {
            StartHidden = args.Any(arg => string.Equals(arg, BackgroundArgument, StringComparison.OrdinalIgnoreCase))
        };
    }
}

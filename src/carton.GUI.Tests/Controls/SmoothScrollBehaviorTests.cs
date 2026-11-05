using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent2;
using Avalonia.Threading;
using carton.GUI.Helpers;
using Xunit;

namespace carton.GUI.Tests.Controls;

[Collection("Avalonia UI")]
public class SmoothScrollBehaviorTests
{
    [Fact]
    public async Task NestedWheelUsesInnerThenOuterAndStopsAfterExtentShrink()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            Application.Current!.Styles.Add(new Fluent2Theme());
            var content = new Border { Height = 2000 };
            var inner = new ScrollViewer { Height = 200, Content = content };
            var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, new Border { Height = 2000 } } } };
            SmoothScrollBehavior.SetIsEnabled(inner, true);
            SmoothScrollBehavior.SetIsEnabled(outer, true);
            var window = new Window { Width = 500, Height = 400, Content = outer };
            window.Show();
            try
            {
                await Task.Delay(80);
                window.MouseWheel(new Point(60, 60), new Vector(0, -1));
                Assert.Equal(0, inner.Offset.Y); // Wheel must schedule, not jump instantly.
                await Task.Delay(150);
                Assert.InRange(inner.Offset.Y, 1, 90);
                Assert.Equal(0, outer.Offset.Y);
                await Task.Delay(450);
                Assert.InRange(inner.Offset.Y, 89.5, 90.5);
                inner.Offset = new Vector(0, inner.Extent.Height - inner.Viewport.Height);
                window.MouseWheel(new Point(60, 60), new Vector(0, -1));
                await Task.Delay(200);
                Assert.True(outer.Offset.Y > 0);
                await Task.Delay(400);
                outer.Offset = default;
                inner.Offset = default;
                window.MouseWheel(new Point(60, 60), new Vector(0, -5));
                content.Height = 40;
                await Task.Delay(200);
                Assert.Equal(0, inner.Offset.Y);
                var settled = outer.Offset;
                await Task.Delay(200);
                Assert.Equal(settled, outer.Offset);
                SmoothScrollBehavior.SetIsEnabled(outer, false);
                window.Close();
                await Task.Delay(60);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}

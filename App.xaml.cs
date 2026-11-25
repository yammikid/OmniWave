using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        ProxyService.Shutdown();
        base.OnExit(e);
    }
}

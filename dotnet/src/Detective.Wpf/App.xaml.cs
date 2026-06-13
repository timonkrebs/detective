using System;
using System.IO;
using System.Threading;
using System.Windows;
using Detective.ViewModels;

namespace Detective.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // On the UI thread SynchronizationContext.Current is a
        // DispatcherSynchronizationContext, so MemoizR reactions are marshaled
        // back onto the dispatcher before they touch bindable properties.
        var viewModel = new DetectiveViewModel(SynchronizationContext.Current)
        {
            RepoPathInput = e.Args.Length > 0 ? e.Args[0] : Directory.GetCurrentDirectory(),
        };

        new MainWindow { DataContext = viewModel }.Show();
    }
}

using System;
using System.Windows;

namespace RefineLauncher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(args.Exception.ToString(), "RefineLauncher error");
                args.Handled = true;
            };

            base.OnStartup(e);
        }
    }
}

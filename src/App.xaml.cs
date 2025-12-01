using System;
using System.Windows;

namespace Taview
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Check for command-line arguments
            string? filePath = null;
            if (e.Args.Length > 0)
            {
                filePath = e.Args[0];
            }

            // Create and show main window
            var mainWindow = new MainWindow(filePath);
            mainWindow.Show();
        }
    }
}

using System.Windows;

namespace GnomeWin
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var topBar = new MainWindow();
            topBar.Show();
        }
    }
}
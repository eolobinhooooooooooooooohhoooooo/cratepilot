using System.Windows;
using System.Windows.Threading;

namespace CratePilot
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs args)
            {
                var window = MainWindow as CratePilot.MainWindow;
                if (window != null) window.HandleUiException(args.Exception);
                else { MessageBox.Show("Não foi possível abrir a interface do CratePilot.", "CratePilot", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
                args.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
    }
}

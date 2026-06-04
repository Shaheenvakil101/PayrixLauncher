using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PayrixLauncher;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            var msg = $"[{DateTime.Now}] DISPATCHER: {e.Exception}\n\n";
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            System.Windows.MessageBox.Show(e.Exception.Message, "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var msg = $"[{DateTime.Now}] UNHANDLED: {e.ExceptionObject}\n\n";
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        try
        {
            var settings = Services.SettingsService.Load();
            string email, name;

            if (!string.IsNullOrWhiteSpace(settings.UserEmail))
            {
                email = settings.UserEmail;
                var local = email.Split('@')[0];
                name = string.Join(" ", local.Split('.').Select(p =>
                    p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
                if (string.IsNullOrWhiteSpace(name)) name = local;
            }
            else
            {
                var raw   = System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? "";
                var parts = raw.Split('\\');
                var user  = parts.Length > 1 ? parts[1] : parts[0];
                email = $"{user.ToLower()}@bqe.com";
                name  = string.Join(" ", user.Split('.').Select(p =>
                    p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
            }

            var main = new MainWindow(name, email);
            System.Windows.Application.Current.MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            var msg = $"[{DateTime.Now}] STARTUP: {ex}\n\n";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            System.Windows.MessageBox.Show(ex.Message, "Startup Error");
            Shutdown();
        }
    }
}

using TrayDrop.Core;

namespace TrayDrop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TrayDrop.Activate");
        using var instance = new Mutex(true, @"Local\TrayDrop.Instance", out var isNew);
        if (!isNew)
        {
            activate.Set();
            return;
        }

        try
        {
            var port = 45823;
            var show = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--show")
                    show = true;
                else if (args[index] == "--port" && ++index < args.Length &&
                         int.TryParse(args[index], out var parsed) && parsed is >= 1 and <= 65535)
                    port = parsed;
                else
                    throw new ArgumentException("Usage: TrayDrop.exe [--show] [--port 1-65535]");
            }

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayDrop");
            var locations = new SaveLocationStore(dataDirectory, WindowsFolders.Downloads());
            using var context = new TrayDropContext(
                new ServerOptions(dataDirectory, port) { ReceiveDirectory = locations.CurrentDirectory }, activate, locations);
            Application.ThreadException += (_, eventArgs) =>
                context.ReportError(eventArgs.Exception, "TrayDrop could not complete this action.");
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                var exception = eventArgs.ExceptionObject as Exception ??
                                new InvalidOperationException("An unknown fatal error occurred.");
                context.ReportError(exception, "TrayDrop encountered a fatal error. Restart the app.");
            };
            context.Start();
            if (show)
                activate.Set();
            Application.Run(context);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "TrayDrop could not start or shut down correctly.\n\n" + exception.Message +
                "\n\nIf the port is already in use, start with --port 45824.",
                "TrayDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
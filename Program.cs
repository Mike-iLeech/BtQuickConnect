namespace BtQuickConnect;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // после автообновления старая копия ещё выходит и держит мьютекс — подождём её
        int i = Array.IndexOf(args, Updater.UpdatedFlag);
        string? updatedFrom = i >= 0 && i + 1 < args.Length ? args[i + 1] : i >= 0 ? "" : null;

        using var mutex = new Mutex(false, @"Local\BtQuickConnect.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(updatedFrom != null ? 15000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;

        try
        {
            Task.Run(Updater.Cleanup);
            ApplicationConfiguration.Initialize();
            using var app = new TrayApp(updatedFrom);
            Application.Run();
        }
        finally { mutex.ReleaseMutex(); }
    }
}

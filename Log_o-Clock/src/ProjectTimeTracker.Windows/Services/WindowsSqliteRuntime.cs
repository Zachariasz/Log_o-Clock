using System.Threading;

namespace ProjectTimeTracker.Windows.Services;

public static class WindowsSqliteRuntime
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        SQLitePCL.raw.FreezeProvider();
    }
}

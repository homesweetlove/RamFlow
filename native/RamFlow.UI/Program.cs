using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;

namespace RamFlow.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool smoke = false;
        string? screenshot = null;
        int? restartFrom = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--smoke-test") smoke = true;
            else if (args[i] == "--screenshot" && i + 1 < args.Length)
            {
                screenshot = args[++i];
                smoke = true;
            }
            else if (args[i] == "--restart-from" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int parentPid) && parentPid > 0)
                restartFrom = int.Parse(args[++i]);
            else
            {
                Console.Error.WriteLine("사용법: RamFlow [--smoke-test] [--screenshot <PNG 경로>]");
                return 2;
            }
        }

        Mutex? mutex = null;
        bool ownsMutex = false;
        App? app = null;
        try
        {
            if (!smoke)
            {
                // The elevated child waits for the previous UI to restore resources and release its mutex.
                if (restartFrom.HasValue)
                {
                    try
                    {
                        using var previous = Process.GetProcessById(restartFrom.Value);
                        if (!previous.WaitForExit(15000))
                            throw new InvalidOperationException("기존 RamFlow의 종료를 기다리는 시간이 초과되었습니다.");
                    }
                    catch (ArgumentException) { /* The parent has already exited. */ }
                }
                using var identity = WindowsIdentity.GetCurrent();
                string sid = identity.User?.Value ?? Environment.UserName;
                mutex = new Mutex(false, @"Local\RamFlow.UI." + sid);
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                {
                    System.Windows.MessageBox.Show("RamFlow가 이미 실행 중입니다. 작업 표시줄 알림 영역에서 RamFlow를 열어 주세요.",
                        "RamFlow", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return 0;
                }
            }

            app = new App(smoke, screenshot);
            return app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (!smoke)
                System.Windows.MessageBox.Show("RamFlow를 시작하지 못했습니다.\n" + ex.Message, "RamFlow",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            app?.Cleanup();
            if (ownsMutex) mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }
}

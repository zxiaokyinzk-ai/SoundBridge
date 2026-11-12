using System.IO;
using System.Text.Json;
using System.Windows;
using AudioRouter.Audio;
using AudioRouter.Windows;

namespace AudioRouter;

public partial class App : Application
{
    private EventWaitHandle? _activateWindow;
    private RegisteredWaitHandle? _activationListener;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0] is "--hardware-test" or "--test-source")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var directory=Path.GetFullPath(e.Args[1]);
            try
            {
                if(e.Args[0]=="--test-source") await Task.Run(()=>AudioHardwareTest.RunSourceAsync(directory));
                else
                {
                    var tests=await Task.Run(()=>AudioHardwareTest.RunAsync(directory));
                    File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new { Success=true,Tests=tests },new JsonSerializerOptions { WriteIndented=true }));
                }
                Shutdown(0);
            }
            catch(Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory,e.Args[0]=="--test-source"?"child-error.txt":"result.json"),JsonSerializer.Serialize(new { Success=false,Error=ex.ToString() },new JsonSerializerOptions { WriteIndented=true }));
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] is "--self-test" or "--diagnostics")
        {
            var path = e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppContext.BaseDirectory, "diagnostics.json");
            try
            {
                object result = e.Args[0] == "--self-test"
                    ? new { Success = true, Tests = AudioEngineSelfTest.Run() }
                    : new { Success = true, Windows = Environment.OSVersion.Version.ToString(), Devices = AudioDeviceService.GetDevices(), Applications = AudioDeviceService.GetApplications(), GameMicrophone = GameMicrophoneCheck.Read() };
                File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(1);
            }
            return;
        }
        base.OnStartup(e);
        _activateWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SoundBridge.ActivateWindow", out var first);
        if (!first)
        {
            _activateWindow.Set();
            Shutdown();
            return;
        }
        _activationListener = ThreadPool.RegisterWaitForSingleObject(_activateWindow, (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is not { } window) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }), null, Timeout.Infinite, false);
        SoundBridgeTheme.Apply(SoundBridgeTheme.Load());
        MainWindow = e.Args.Contains("--launcher") ? new LauncherWindow() : new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListener?.Unregister(null);
        _activateWindow?.Dispose();
        base.OnExit(e);
    }
}

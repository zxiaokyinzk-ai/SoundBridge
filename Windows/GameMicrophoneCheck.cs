using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioRouter.Windows;

internal sealed record RecordingSession(int ProcessId, string Application, string Device);

internal sealed record GameMicrophoneCheck(string DefaultInput, string CommunicationsInput,
    bool DeltaForceRunning, IReadOnlyList<RecordingSession> ActiveSessions)
{
    private static bool IsDeltaForce(string name) =>
        name.Equals("DeltaForceClient-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BHDClient-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DeltaForceClient", StringComparison.OrdinalIgnoreCase);

    internal static GameMicrophoneCheck Read()
    {
        using var enumerator = new MMDeviceEnumerator();
        string DefaultName(Role role)
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, role, out var device)) return "无";
            using (device) return device!.FriendlyName;
        }

        var sessions = new HashSet<RecordingSession>();
        var processes = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { processes[process.Id] = process.ProcessName; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        AudioDeviceService.VisitSessions((session, device) =>
        {
            if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) return;
            int pid = (int)session.GetProcessID;
            sessions.Add(new(pid, processes.GetValueOrDefault(pid) ?? $"PID {pid}", device.FriendlyName));
        }, DataFlow.Capture);
        return new(DefaultName(Role.Multimedia), DefaultName(Role.Communications),
            processes.Values.Any(IsDeltaForce), sessions.OrderByDescending(s => IsDeltaForce(s.Application))
            .ThenBy(s => s.Application).ToList());
    }

    public string Describe()
    {
        var lines = new List<string> { $"默认录音设备：{DefaultInput}　｜　默认通信设备：{CommunicationsInput}" };
        var game = ActiveSessions.Where(s => IsDeltaForce(s.Application)).ToList();
        if (game.Count > 0)
            lines.Add("三角洲行动正在使用：" + string.Join("、", game.Select(s => s.Device).Distinct()));
        else
            lines.Add(DeltaForceRunning ? "三角洲行动已启动，尚未看到游戏进程打开录音设备。进入小队并开启语音后再看此处。"
                : "尚未检测到三角洲行动客户端。进游戏后可在这里查看实际话筒。");
        foreach (var session in ActiveSessions.Except(game).Where(s => s.ProcessId != Environment.ProcessId))
            lines.Add($"{session.Application} 正在使用：{session.Device}");
        return string.Join(Environment.NewLine, lines);
    }
}

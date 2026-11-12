# Windows audio integration

`AudioDeviceService.GetDevices()` returns active input/output endpoints. `GetApplications()` returns apps with non-expired audio sessions. Same-executable parent/child processes are grouped under their ancestor; `RoutingProcessIds` contains only the PIDs that have audio sessions. Use the ancestor for WASAPI process-tree capture and apply primary-device routing only to each listed audio-session PID. The UI refreshes this list before starting a route and checks the parent creation time. Windows build 26200 returns E_INVALIDARG when querying a fresh process without any initialized audio stream; the UI asks the user to play audio first instead of assuming a default route.

`AudioPolicyRouter` changes only the chosen application's Console and Multimedia render preferences. It does not change system defaults or the Communications role. `TryGetApplicationOutput` returns success with `deviceId == null` when the app follows the system default. A failure returns false and a readable error. `Capture` snapshots both independent roles and process creation time. Restore with `TryRestore` on stop/close. Native volumes are not modified or restored: user changes in the original app are preserved. Route mutation validates an active render endpoint and rolls back partial role changes.

The per-app routing ABI is an **undocumented Windows implementation detail**, also used by EarTrumpet. Future Windows versions may change it. Apps using exclusive mode or explicitly selected devices may ignore the default-device override; those apps need their own audio settings, or Windows Volume Mixer. A process that exits before restoration cannot reliably have its persisted override restored using its old PID. Errors must stay visible. Do not call any global reset API.

Native API contract references:

- https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/MMDeviceAPI/IAudioPolicyConfigFactoryVariantFor21H2.cs
- https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/MMDeviceAPI/IAudioPolicyConfigFactoryVariantForDownlevel.cs
- https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs
- https://learn.microsoft.com/windows/win32/api/roapi/nf-roapi-rogetactivationfactory

This implementation declares raw ABI function pointers independently, avoiding removed .NET WinRT marshalling. IInspectable occupies the first 6 vtable slots; the 19 preceding audio-policy methods make Set/Get slots 25/26. HSTRINGs and interface references are always released. Discovery uses NAudio 3.3 public wrapper APIs and Toolhelp process snapshots.

When copying one application's audio into additional outputs, exclude its primary Windows output from captured destinations to avoid duplicated playback. Never route the source app's original stream to the same virtual cable that receives the application's mixed output. Virtual microphone input requires a separately installed virtual audio device driver.

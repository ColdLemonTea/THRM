# THRM Avalonia POC

This is a deliberately small desktop client for the existing THRM Core. It
does not replace the Go Core, `THRM-IPC`, configuration storage, or
`TempBridge`.

## Build and checks

From the repository root on Windows:

```powershell
& "$env:USERPROFILE\scoop\apps\dotnet-sdk\current\dotnet.exe" build gui/THRM.Avalonia/THRM.Avalonia.csproj
& "$env:USERPROFILE\scoop\apps\dotnet-sdk\current\dotnet.exe" run --project gui/THRM.Avalonia/THRM.Avalonia.csproj -- --self-check
```

The client uses `THRM-IPC` and falls back to the legacy endpoint name. Set
`THRM_IPC_PIPE_NAME` to isolate a local Core test endpoint, matching the Go
Core test hook.

Windows uses `NamedPipeClientStream`. Linux uses the existing Unix socket at
`/tmp/THRM-IPC.sock`. The desktop bootstrap uses the official
`UsePlatformDetect().UseWaylandWithFallback()` route: native Wayland is
preferred when `WAYLAND_DISPLAY` and the required native libraries are
available, while Avalonia may fall back to X11/XWayland when Wayland cannot be
started.

For a CachyOS KDE acceptance run, log into a Plasma Wayland session, verify
`echo $XDG_SESSION_TYPE` reports `wayland` and `echo $WAYLAND_DISPLAY` is
non-empty, install the Avalonia Wayland/X11 native runtime dependencies, then
run the Linux build from this directory and confirm the window opens, IPC
connects to `/tmp/THRM-IPC.sock`, and the UI receives temperature and fan
events. Also repeat with `WAYLAND_DISPLAY` unset or an invalid value to verify
the documented X11/XWayland fallback; this Windows worktree cannot claim that
real-machine acceptance.

The recovery loop reconnects after transport loss and refreshes read-only
state. A write that fails after it may have reached Core is reported to the
user and is never replayed automatically.

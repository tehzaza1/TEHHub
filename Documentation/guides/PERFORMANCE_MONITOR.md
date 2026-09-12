# Background performance monitor

Use the regular Release TEHhub while playing. Debug is not required. The deployment directory can remain `C:\Games\Hy-v Tool\DXPEOE\GameHelper2`.

1. Start the updated TEHhub normally.
2. Double-click `Start-PerformanceMonitor.bat` beside TEHhub.exe.
3. Play normally. The helper runs hidden, measures 20-second intervals approximately every three minutes, and leaves instrumentation off between rounds.
4. Stop with `Stop-PerformanceMonitor.bat`. It requests a clean stop without killing TEHhub.

Reports are in `logs/performance-monitor`, beside the deployed application:

- `status.json`: helper PID, waiting/capturing/resting/stopped state and latest report path.
- `latest-summary.json`: latest measured render p99 peak, working set, memory failures and inclusive scope ranking.
- `round-<time>-<id>.json`: sampled JSON with build/version/PID/capture identity and world/entity context. Keep the newest 30 rounds.
- `trend-history.jsonl`: compact per-round RAM, render and read-failure trends for longer play sessions; rotate after about 5 MiB with one previous file retained.

The helper uses only the localhost diagnostics API, does not open the game process itself and does not alter plugins or settings. It waits/retries when TEHhub is closed or an older build lacks the endpoints. One instance per report directory is permitted by a named mutex. It does not reset another active capture. Matching PID and capture IDs prevent mixing data from restarts; stop requests only target the helper's own capture.

Sampling has overhead during those 20 seconds, particularly memory bookkeeping and profiler scopes. These are measurements for analysis, not automatic repair or proof that a plugin/offset is broken. Render duration is not game FPS or GPU time; nested scopes overlap. An empty/error report is not a clean health certificate.

Optional manual settings:

```powershell
.\tools\Watch-Bottlenecks.ps1 -Mode Start -CaptureSeconds 15 -IntervalSeconds 300
.\tools\Watch-Bottlenecks.ps1 -Mode Status
.\tools\Watch-Bottlenecks.ps1 -Mode Stop
```

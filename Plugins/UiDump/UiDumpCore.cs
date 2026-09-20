namespace UiDump;

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickableTransparentOverlay.Win32;
using ImGuiNET;
using TEHhub;
using TEHhub.Plugin;
using TEHhub.Utils;

public sealed class UiDumpSettings : IPSettings
{
    public VK Hotkey = VK.F8;
    public int DelaySeconds = 3;
    public bool IncludeHidden;
    public bool IncludeRaw = true;
    public int MaxNodes = 20000;
    public string Label = "stash";
}

[JsonSourceGenerationOptions(IncludeFields = true, WriteIndented = true)]
[JsonSerializable(typeof(UiDumpSettings))]
[JsonSerializable(typeof(UiSnapshot))]
internal sealed partial class UiDumpJson : JsonSerializerContext { }

/// <summary>On-demand read-only capture of the currently attached game's UI tree.</summary>
public sealed partial class UiDumpCore : PCore<UiDumpSettings>
{
    private UiCapture? capture;
    private Task<string>? saving;
    private long dueAt;
    private long startedAt;
    private bool keyWasDown;
    private string status = "Open an in-game panel, then press F8 or Capture.";
    private string lastFile = "";
    private IntPtr capturedRoot;
    private long previousFrameAt;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    public override string GetDescription() => "Dump the open game's UI tree to a ZIP (JSON + readable text). Read-only, on demand.";

    public override void OnEnable(bool isGameOpened)
    {
        try
        {
            var path = this.PluginConfigPath("settings.json");
            if (File.Exists(path)) this.Settings = JsonSerializer.Deserialize(File.ReadAllText(path), UiDumpJson.Default.UiDumpSettings) ?? new();
        }
        catch (Exception error) { this.status = "Settings not loaded: " + error.Message; }
        this.keyWasDown = true; // Require release before first hotkey capture.
    }

    public override void SaveSettings()
    {
        Directory.CreateDirectory(this.PluginConfigDirectory);
        File.WriteAllText(this.PluginConfigPath("settings.json"), JsonSerializer.Serialize(this.Settings, UiDumpJson.Default.UiDumpSettings));
    }

    public override void OnDisable()
    {
        this.dueAt = 0;
        if (this.capture != null)
        {
            this.capture.Abort("Plugin disabled during capture");
            this.SaveCapture();
        }
    }

    public override void DrawSettings()
    {
        // Settings can remain visible while the game is not the foreground window. PManager
        // deliberately suppresses DrawUI in that situation, so drive pending/capture/save work
        // here too. The overlay is not part of PoE's GameUi tree and cannot pollute the dump.
        this.AdvanceCaptureWork(allowHotkey: false);

        ImGui.TextWrapped("Open the game UI you want to inspect. Keep the same panel/tab open until capture finishes.");
        ImGui.InputText("Capture label", ref this.Settings.Label, 80);
        ImGuiHelper.NonContinuousEnumComboBox("Capture hotkey", ref this.Settings.Hotkey);
        ImGui.SliderInt("Hotkey delay (seconds)", ref this.Settings.DelaySeconds, 0, 10);
        ImGui.Checkbox("Include hidden UI subtrees", ref this.Settings.IncludeHidden);
        ImGui.Checkbox("Include raw UI bytes for research", ref this.Settings.IncludeRaw);
        ImGui.SliderInt("Maximum nodes", ref this.Settings.MaxNodes, 1000, 50000);
        bool busy = this.dueAt != 0 || this.capture != null || this.saving != null;
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Capture current game UI"))
        {
            // A settings-button capture is immediate and continues from DrawSettings. Requiring
            // the user to focus the game first made the original request stall because DrawUI is
            // hidden whenever TEHhub is foreground and HideOverlaysWhenGameInactive is enabled.
            this.StartCapture(Environment.TickCount64);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            this.dueAt = 0;
            if (this.capture != null)
            {
                this.capture.Abort("Cancelled by user");
                this.SaveCapture();
            }
            else if (this.saving == null) this.status = "Cancelled";
        }
        ImGui.TextWrapped(this.status);
        if (!string.IsNullOrEmpty(this.lastFile))
        {
            ImGui.TextWrapped(this.lastFile);
            if (ImGui.Button("Copy ZIP path")) ImGui.SetClipboardText(this.lastFile);
            ImGui.SameLine();
            if (ImGui.Button("Open dump folder"))
            {
                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(this.lastFile)!) { UseShellExecute = true }); }
                catch (Exception error) { this.status = "Could not open folder: " + error.Message; }
            }
        }
    }

    public override void DrawUI()
    {
        this.AdvanceCaptureWork(allowHotkey: true);
    }

    private void AdvanceCaptureWork(bool allowHotkey)
    {
        if (this.saving?.IsCompleted == true)
        {
            try { this.lastFile = this.saving.GetAwaiter().GetResult(); this.status += " | ZIP saved."; }
            catch (Exception error) { this.status = "Save failed: " + error.Message; }
            this.saving = null;
        }
        if (allowHotkey)
        {
            bool down = (GetAsyncKeyState((int)this.Settings.Hotkey) & 0x8000) != 0;
            if (down && !this.keyWasDown && Core.Process.Foreground) this.Schedule();
            this.keyWasDown = down;
        }
        long now = Environment.TickCount64;
        if (this.dueAt != 0 && now >= this.dueAt)
        {
            this.dueAt = 0;
            this.StartCapture(now);
        }
        if (this.capture == null) return;
        try
        {
            var game = Core.States.InGameStateObject;
            if (Core.Process.Pid != this.capture.Snapshot.GameProcessId ||
                game?.GameUi?.Address != this.capturedRoot ||
                game?.CurrentAreaInstance?.AreaHash != this.capture.Snapshot.AreaHash ||
                Core.States.GameCurrentState.ToString() != this.capture.Snapshot.GameState)
                this.capture.Abort("Game process, area, UI root or game state changed");
            else if (now - this.startedAt > 15000 || now - this.previousFrameAt > 2000)
                this.capture.Abort("Capture timed out or rendering was suspended");
            else this.capture.Step(address => UiMemoryReader.Read(address, this.capture.Snapshot.IncludeRaw));
            this.previousFrameAt = now;
        }
        catch (Exception error) { this.capture.Abort("Capture failed: " + error.Message); }
        this.status = $"{this.capture.Snapshot.Nodes.Count} UI nodes captured";
        if (this.capture.Done) this.SaveCapture();
    }

    private void Schedule()
    {
        if (this.capture != null || this.saving != null || this.dueAt != 0) return;
        this.dueAt = Environment.TickCount64 + Math.Clamp(this.Settings.DelaySeconds, 0, 10) * 1000L;
        this.status = $"Capture scheduled in {Math.Clamp(this.Settings.DelaySeconds, 0, 10)} seconds. Return to the game panel.";
    }

    private void StartCapture(long now)
    {
        if (this.capture != null || this.saving != null)
        {
            return;
        }

        var game = Core.States.InGameStateObject;
        var root = game?.GameUi?.Address ?? IntPtr.Zero;
        var gameState = Core.States.GameCurrentState;
        var areaHash = game?.CurrentAreaInstance?.AreaHash ?? string.Empty;
        if (Core.Process.Pid == 0 || root == IntPtr.Zero)
        {
            this.status =
                $"Game UI unavailable: PID={Core.Process.Pid}, State={gameState}, " +
                $"Root=0x{root.ToInt64():X}, AreaHash='{areaHash}', Foreground={Core.Process.Foreground}.";
            return;
        }

        // PoE2 0.5.x can be reported as UnknownState while the InGameState and GameUi roots are
        // already valid. The root is the authority for a read-only UI dump; record the state as
        // evidence instead of rejecting an otherwise readable tree.
        this.capturedRoot = root;
        this.startedAt = this.previousFrameAt = now;
        this.capture = new UiCapture(root.ToInt64(), new UiSnapshot
        {
            Label = this.Settings.Label,
            GameProcessId = (int)Core.Process.Pid,
            AreaHash = areaHash,
            GameState = gameState.ToString(),
            RootAddress = $"0x{root.ToInt64():X}",
            Build = typeof(Core).Assembly.GetName().Version?.ToString() ?? "unknown",
            WindowRectangle = Core.Process.WindowArea.ToString(),
            IncludeHidden = this.Settings.IncludeHidden,
            IncludeRaw = this.Settings.IncludeRaw,
        }, Math.Clamp(this.Settings.MaxNodes, 1000, 50000));
        this.status =
            $"Capture started: PID={Core.Process.Pid}, State={gameState}, " +
            $"Root=0x{root.ToInt64():X}, AreaHash='{areaHash}'.";
    }

    private void SaveCapture()
    {
        var snapshot = this.capture!.Snapshot;
        this.capture = null;
        this.status = $"{snapshot.Status}: {snapshot.Nodes.Count} nodes, {snapshot.Issues.Count} issues. Saving...";
        string directory = this.PluginConfigPath("dumps");
        // Snapshot is no longer mutated. Background work performs file IO only, never game reads.
        this.saving = Task.Run(() => WriteArchive(directory, snapshot));
    }

    private static string WriteArchive(string directory, UiSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        string filename = $"ui-{snapshot.StartedUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip";
        string finalPath = Path.Combine(directory, filename);
        string temporaryPath = finalPath + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("ui.json").Open())
                    JsonSerializer.Serialize(stream, snapshot, UiDumpJson.Default.UiSnapshot);
                using (var text = new StreamWriter(zip.CreateEntry("ui-tree.txt").Open(), Encoding.UTF8))
                {
                    text.WriteLine($"{snapshot.Status}\nLabel: {snapshot.Label}\n{snapshot.StartedUtc:O} -> {snapshot.FinishedUtc:O}");
                    foreach (var node in snapshot.Nodes)
                    {
                        text.WriteLine($"{node.Path} {node.Address} visible={node.EffectiveVisible} flags=0x{node.Flags:X} children={node.DeclaredChildren}");
                        foreach (var pair in node.TextCandidates)
                            text.WriteLine($"  text candidate {pair.Key}: {JsonSerializer.Serialize(pair.Value)}");
                        if (node.ItemPath != null) text.WriteLine($"  item: {node.ItemPath} ({node.ItemAddress})");
                        if (node.ChildrenOmitted != null) text.WriteLine($"  {node.ChildrenOmitted}");
                        foreach (var error in node.Errors) text.WriteLine("  ERROR: " + error);
                    }
                    text.WriteLine("\nCapture issues:");
                    foreach (var issue in snapshot.Issues) text.WriteLine(issue);
                }
                using var readme = new StreamWriter(zip.CreateEntry("README.txt").Open(), Encoding.UTF8);
                readme.WriteLine("Read-only PoE2 GameUi capture. Captured incrementally over render frames, NOT an atomic memory snapshot. Keep the panel/tab stable.\nui.json contains paths, parent links, local/effective visibility, geometry, text candidates, validated item pointers and optional raw node bytes.\nScreenRect is [x,y,width,height] in the existing TEHhub UI coordinate system; WindowRectangle records the game window. Null means unavailable.\nText offsets 0x128, 0x2E0, 0x360 and item candidate 0x4E0 follow existing PoE2 readers, not PoE1 layouts. TextCandidates are NOT guaranteed display labels.\nComplete means the selected tree scope was traversed without reported errors; it does not mean every UI-specific field or string was decoded. Hidden subtrees are omitted unless IncludeHidden is true.\nTraversal limits: configurable node limit, 64 levels, 2048 children/node, 15 seconds total; partial captures explicitly list issues. RawHex covers 0x500 bytes beginning at the node address.\nCapture once with stash closed, then separately for each open tab or panel. Send this ZIP with a matching screenshot and the game version for research.");
            }
            File.Move(temporaryPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }
}

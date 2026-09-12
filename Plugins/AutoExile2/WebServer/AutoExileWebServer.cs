// <copyright file="AutoExileWebServer.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.WebServer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using AutoExile2.Systems;
    using ClickableTransparentOverlay.Win32;
    using TEHhub;
    using TEHhub.RemoteObjects.Components;

    /// <summary>
    /// Embedded HTTP and WebSocket server for AutoExile 2 live dashboard, settings, and profile management.
    /// </summary>
    public class AutoExileWebServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonSettings = new(AutoExileJson.Options)
        {
            WriteIndented = false,
        };

        private HttpListener? listener;
        private CancellationTokenSource? cts;
        private Task? listenTask;
        private Task? broadcastTask;
        private readonly int port;
        private readonly bool networkAccess;
        private AutoExile2Settings settings;
        private readonly ProfileManager profileManager;
        private readonly Func<AutoExileStatusSnapshot> statusProvider;
        private readonly Action saveSettingsCallback;
        private readonly Func<string> dumpCallback;
        private readonly Func<IEnumerable<AutoExile2.Modes.IBotMode>>? modesProvider;
        private readonly CoopVirtualGamepad? coopGamepad;
        private readonly List<WebSocket> wsClients = new();
        private readonly object wsLock = new();

        public AutoExileWebServer(
            int port,
            bool networkAccess,
            AutoExile2Settings settings,
            ProfileManager profileManager,
            Func<AutoExileStatusSnapshot> statusProvider,
            Action saveSettingsCallback,
            Func<string> dumpCallback,
            Func<IEnumerable<AutoExile2.Modes.IBotMode>>? modesProvider = null,
            CoopVirtualGamepad? coopGamepad = null)
        {
            this.port = port;
            this.networkAccess = networkAccess;
            this.settings = settings;
            this.profileManager = profileManager;
            this.statusProvider = statusProvider;
            this.saveSettingsCallback = saveSettingsCallback;
            this.dumpCallback = dumpCallback;
            this.modesProvider = modesProvider;
            this.coopGamepad = coopGamepad;
        }

        public string Url => $"http://localhost:{this.port}/";
        public bool IsRunning => this.listener?.IsListening == true;

        public void Start()
        {
            if (this.IsRunning) return;

            try
            {
                this.listener = new HttpListener();
                string prefix = this.networkAccess ? $"http://+:{this.port}/" : $"http://localhost:{this.port}/";

                try
                {
                    this.listener.Prefixes.Add(prefix);
                    this.listener.Start();
                }
                catch (HttpListenerException)
                {
                    // Fallback to localhost if http://+ requires urlacl
                    this.listener.Close();
                    this.listener = new HttpListener();
                    this.listener.Prefixes.Add($"http://localhost:{this.port}/");
                    this.listener.Start();
                }

                this.cts = new CancellationTokenSource();
                this.listenTask = Task.Run(() => this.ListenLoop(this.cts.Token));
                this.broadcastTask = Task.Run(() => this.BroadcastLoop(this.cts.Token));
                Console.WriteLine($"[AutoExile2] Web Dashboard started at {this.Url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoExile2] Failed to start Web Dashboard: {ex.Message}");
            }
        }

        public void Stop()
        {
            this.cts?.Cancel();
            try { this.listener?.Stop(); } catch { }
            try { this.listener?.Close(); } catch { }
            this.listener = null;

            lock (this.wsLock)
            {
                foreach (var ws in this.wsClients)
                {
                    try { ws.Dispose(); } catch { }
                }
                this.wsClients.Clear();
            }
        }

        public void Dispose()
        {
            this.Stop();
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && this.listener != null && this.listener.IsListening)
            {
                try
                {
                    var ctx = await this.listener.GetContextAsync();
                    _ = Task.Run(() => this.HandleRequest(ctx, token));
                }
                catch (HttpListenerException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx, CancellationToken token)
        {
            try
            {
                if (ctx.Request.IsWebSocketRequest)
                {
                    await this.HandleWebSocket(ctx);
                    return;
                }

                string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
                string method = ctx.Request.HttpMethod.ToUpperInvariant();

                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    return;
                }

                if (path == "" || path == "/" || path == "/index.html" || path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".png") || path.EndsWith(".ico") || path.EndsWith(".svg"))
                {
                    await StaticFileHandler.ServeStaticFile(ctx, path);
                    return;
                }

                if (path == "/api/status" && method == "GET")
                {
                    var snap = this.statusProvider();
                    await this.SendJson(ctx, snap);
                    return;
                }

                if (path == "/api/cooldowns" && method == "GET")
                {
                    var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                    var player = area?.Player;
                    var result = new List<object>();
                    var buffsList = new List<object>();

                    if (player != null && player.TryGetComponent<Buffs>(out var pBuffs) && pBuffs.StatusEffects != null)
                    {
                        foreach (var (bName, sEff) in pBuffs.StatusEffects)
                        {
                            buffsList.Add(new
                            {
                                name = bName,
                                charges = sEff.Charges,
                                timeLeft = sEff.TimeLeft,
                                totalTime = sEff.TotalTime,
                                effectiveness = sEff.Effectiveness
                            });
                        }
                    }

                    if (player != null && player.TryGetComponent<Actor>(out var actor))
                    {
                        var reader = Core.Process.Handle;
                        foreach (var (skillName, details) in actor.ActiveSkills)
                        {
                            var cdData = new Dictionary<string, object>();
                            cdData["skillName"] = skillName;
                            cdData["unknownId"] = $"0x{details.UnknownIdAndEquipmentInfo:X}";
                            cdData["totalCooldownTimeMs"] = details.TotalCooldownTimeInMs;
                            cdData["totalCooldownTimeSec"] = details.TotalCooldownTimeInMs / 1000.0;
                            cdData["totalUses"] = details.TotalUses;
                            cdData["isUsable"] = actor.IsSkillUsable.Contains(skillName);

                            if (actor.ActiveSkillCooldowns.TryGetValue(details.UnknownIdAndEquipmentInfo, out var cd))
                            {
                                cdData["cd_activeSkillDatId"] = cd.ActiveSkillsDatId;
                                cdData["cd_maxUses"] = cd.MaxUses;
                                cdData["cd_totalCooldownMs"] = cd.TotalCooldownTimeInMs;
                                cdData["cd_activeCooldownCount"] = cd.TotalActiveCooldowns();
                                cdData["cd_cannotBeUsed"] = cd.CannotBeUsed();

                                var entries = new List<object>();
                                int activeCount = cd.TotalActiveCooldowns();
                                if (activeCount > 0 && cd.CooldownsList.First != IntPtr.Zero)
                                {
                                    for (int e = 0; e < Math.Min(activeCount, 10); e++)
                                    {
                                        IntPtr elemPtr = cd.CooldownsList.First + (e * 0x10);
                                        int i0 = reader.ReadMemory<int>(elemPtr);
                                        int i1 = reader.ReadMemory<int>(elemPtr + 4);
                                        int i2 = reader.ReadMemory<int>(elemPtr + 8);
                                        int i3 = reader.ReadMemory<int>(elemPtr + 12);
                                        float f0 = reader.ReadMemory<float>(elemPtr);
                                        float f1 = reader.ReadMemory<float>(elemPtr + 4);
                                        float f2 = reader.ReadMemory<float>(elemPtr + 8);
                                        float f3 = reader.ReadMemory<float>(elemPtr + 12);
                                        entries.Add(new { ints = new[] { i0, i1, i2, i3 }, floats = new[] { f0, f1, f2, f3 } });
                                    }
                                }
                                cdData["cd_entries"] = entries;
                            }

                            result.Add(cdData);
                        }
                    }

                    await this.SendJson(ctx, new { skills = result, buffs = buffsList });
                    return;
                }

                if (path == "/api/players/nearby" && method == "GET")
                {
                    var snap = this.statusProvider();
                    await this.SendJson(ctx, new
                    {
                        leader = snap.LeaderPlayerName,
                        currentLock = this.settings.FollowerCharacterName,
                        players = snap.NearbyPlayers,
                        playerNames = snap.NearbyPlayerNames
                    });
                    return;
                }

                if ((path == "/api/toggle" || path == "/api/start" || path == "/api/stop") && method == "POST")
                {
                    if (path == "/api/start") this.settings.IsRunning = true;
                    else if (path == "/api/stop") this.settings.IsRunning = false;
                    else this.settings.IsRunning = !this.settings.IsRunning;

                    if (this.settings.IsRunning && this.settings.Mode == AutoExile2.AutoExileMode.Follower)
                    {
                        this.coopGamepad?.EnsureConnected(true, this.settings.CoopPhysicalPadIndex, true);
                    }
                    else if (!this.settings.IsRunning)
                    {
                        BotInput.ReleaseAllMovementKeys(this.settings);
                        this.coopGamepad?.Disconnect();
                    }

                    this.saveSettingsCallback();
                    var snap = this.statusProvider();
                    await this.SendJson(ctx, new { success = true, isRunning = this.settings.IsRunning, status = snap });
                    return;
                }

                // --- Co-op Virtual Controller Connect Route ---
                if (path == "/api/coop/connect" && method == "POST")
                {
                    if (this.coopGamepad != null && this.coopGamepad.EnsureConnected(true, this.settings.CoopPhysicalPadIndex, true))
                    {
                        await this.SendJson(ctx, new
                        {
                            success = true,
                            message = $"Virtual gamepads connected! P1 (Leader) = Slot #{this.coopGamepad.LeaderSlotIndex}, P2 (Follower) = Slot #{this.coopGamepad.FollowerSlotIndex}",
                            followerSlot = this.coopGamepad.FollowerSlotIndex,
                            leaderSlot = this.coopGamepad.LeaderSlotIndex,
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 503;
                        await this.SendJson(ctx, new { success = false, error = "Virtual Gamepads could not be initialized. ViGEm driver may not be installed." });
                    }
                    return;
                }

                // --- Co-op Virtual Controller Test Route ---
                if (path == "/api/coop/test" && method == "POST")
                {
                    if (this.coopGamepad != null && this.coopGamepad.EnsureConnected(true, this.settings.CoopPhysicalPadIndex, true))
                    {
                        this.coopGamepad.TestWobbleFollower();
                        await this.SendJson(ctx, new
                        {
                            success = true,
                            message = $"Follower Controller connected to Slot #{this.coopGamepad.FollowerSlotIndex} and tested (Wobble Verified)!",
                            followerSlot = this.coopGamepad.FollowerSlotIndex,
                            leaderSlot = this.coopGamepad.LeaderSlotIndex,
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 503;
                        await this.SendJson(ctx, new { success = false, error = "Virtual Gamepads could not be initialized. ViGEm driver may not be installed." });
                    }
                    return;
                }

                // --- Co-op Virtual Controller Disconnect Route ---
                if (path == "/api/coop/disconnect" && method == "POST")
                {
                    this.coopGamepad?.Disconnect();
                    await this.SendJson(ctx, new
                    {
                        success = true,
                        message = "Virtual gamepads disconnected.",
                        followerSlot = -1,
                        leaderSlot = -1,
                    });
                    return;
                }

                // --- Range Preview API (Real-time in-game circle visualization when tweaking sliders) ---
                if (path == "/api/preview_range" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid preview payload.");
                    string type = jObj["type"]?.ToString() ?? "Generic";
                    float radius = jObj["radius"]?.Value<float>() ?? 0f;
                    string unit = jObj["unit"]?.ToString() ?? "world";
                    string label = jObj["label"]?.ToString() ?? string.Empty;
                    string color = jObj["color"]?.ToString() ?? "#f43f5e";
                    string target = jObj["targetEntity"]?.ToString() ?? "Leader";

                    RangeVisualizer.SetPreview(type, radius, unit, label, color, target, 3.0f);
                    await this.SendJson(ctx, new { success = true, preview = type, radius = radius });
                    return;
                }

                // --- System Metadata API (Single source of truth for frontend) ---
                if (path == "/api/metadata" && method == "GET")
                {
                    await this.SendJson(ctx, MetadataHandler.GetMetadata(this.modesProvider?.Invoke()));
                    return;
                }

                // --- Profiles API ---
                if (path == "/api/profiles" && method == "GET")
                {
                    await this.SendJson(ctx, new
                    {
                        success = true,
                        active = this.profileManager.ActiveProfileName,
                        profiles = this.profileManager.ListProfiles(),
                    });
                    return;
                }

                if (path == "/api/profiles/switch" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid profile payload.");
                    string name = jObj["name"]?.ToString() ?? "";
                    bool ok = this.profileManager.SwitchProfile(ref this.settings, name);
                    if (!ok)
                    {
                        ctx.Response.StatusCode = 404;
                        await this.SendJson(ctx, new { success = false, error = "Profile not found" });
                        return;
                    }
                    this.saveSettingsCallback();
                    await this.SendJson(ctx, new { success = true, active = this.profileManager.ActiveProfileName, settings = this.settings });
                    return;
                }

                if (path == "/api/profiles/create" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid profile payload.");
                    string name = jObj["name"]?.ToString() ?? "";
                    bool switchTo = jObj["switchTo"]?.Value<bool>() ?? true;
                    bool ok = this.profileManager.CreateProfile(this.settings, name, switchTo);
                    if (!ok)
                    {
                        ctx.Response.StatusCode = 400;
                        await this.SendJson(ctx, new { success = false, error = "Profile already exists or invalid name" });
                        return;
                    }
                    this.saveSettingsCallback();
                    await this.SendJson(ctx, new { success = true, active = this.profileManager.ActiveProfileName, profiles = this.profileManager.ListProfiles() });
                    return;
                }

                if (path == "/api/profiles/rename" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid profile payload.");
                    string from = jObj["from"]?.ToString() ?? "";
                    string to = jObj["to"]?.ToString() ?? "";
                    bool ok = this.profileManager.RenameProfile(from, to);
                    if (!ok)
                    {
                        ctx.Response.StatusCode = 400;
                        await this.SendJson(ctx, new { success = false, error = "Rename failed" });
                        return;
                    }
                    await this.SendJson(ctx, new { success = true, active = this.profileManager.ActiveProfileName, profiles = this.profileManager.ListProfiles() });
                    return;
                }

                if (path == "/api/profiles/delete" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid profile payload.");
                    string name = jObj["name"]?.ToString() ?? "";
                    bool ok = this.profileManager.DeleteProfile(name);
                    if (!ok)
                    {
                        ctx.Response.StatusCode = 400;
                        await this.SendJson(ctx, new { success = false, error = "Cannot delete profile (active profile or not found)" });
                        return;
                    }
                    await this.SendJson(ctx, new { success = true, active = this.profileManager.ActiveProfileName, profiles = this.profileManager.ListProfiles() });
                    return;
                }

                if (path == "/api/profiles/export" && method == "GET")
                {
                    string name = ctx.Request.QueryString["name"] ?? this.profileManager.ActiveProfileName;
                    string? json = this.profileManager.ExportProfile(name);
                    if (json == null)
                    {
                        ctx.Response.StatusCode = 404;
                        await this.SendJson(ctx, new { success = false, error = "Profile not found" });
                        return;
                    }
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                    return;
                }

                if (path == "/api/profiles/import" && method == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Invalid profile payload.");
                    string name = jObj["name"]?.ToString() ?? "";
                    string content = jObj["content"]?.ToString() ?? "";
                    bool ok = this.profileManager.ImportProfile(name, content);
                    if (!ok)
                    {
                        ctx.Response.StatusCode = 400;
                        await this.SendJson(ctx, new { success = false, error = "Import failed (invalid format)" });
                        return;
                    }
                    await this.SendJson(ctx, new { success = true, profiles = this.profileManager.ListProfiles() });
                    return;
                }

                // --- Settings API ---
                if (path == "/api/settings")
                {
                    if (method == "GET")
                    {
                        await this.SendJson(ctx, this.settings);
                    }
                    else if (method == "POST")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            SettingsHandler.UpdateSettingsFromJson(this.settings, body);
                            this.saveSettingsCallback();
                        }

                        await this.SendJson(ctx, new { success = true, settings = this.settings });
                    }
                    return;
                }

                if (path == "/api/skills/detected" && method == "GET")
                {
                    var snap = this.statusProvider();
                    await this.SendJson(ctx, new {
                        success = true,
                        skills = snap.DetectedSkills,
                        p1Skills = snap.DetectedSkills,
                        p2Skills = snap.P2DetectedSkills,
                        leaderName = snap.LeaderPlayerName,
                        followerName = snap.FollowerPlayerName
                    });
                    return;
                }

                if (path == "/api/skills/auto-populate" && method == "POST")
                {
                    var snap = this.statusProvider();
                    if (snap.DetectedSkills.Count > 0)
                    {
                        var newSlots = new List<SkillSlotConfig>();
                        var keysToUse = new[] { VK.KEY_Q, VK.KEY_W, VK.KEY_E, VK.KEY_R, VK.KEY_T };
                        int keyIdx = 0;
                        bool hasAttack = false;

                        foreach (var det in snap.DetectedSkills)
                        {
                            var slot = new SkillSlotConfig
                            {
                                Enabled = true,
                                Name = det.Name,
                                AssignedSkillName = det.Name,
                                Category = det.Category,
                            };
                            SkillClassifier.ApplyCategoryDefaults(slot, det.Category);

                            if (!hasAttack && det.Category == SkillClassifier.CategoryAttack)
                            {
                                slot.InputType = AttackInputType.MouseRight;
                                slot.Key = VK.KEY_Q;
                                hasAttack = true;
                            }
                            else
                            {
                                slot.InputType = AttackInputType.KeyboardKey;
                                slot.Key = keyIdx < keysToUse.Length ? keysToUse[keyIdx++] : VK.KEY_Q;
                            }

                            newSlots.Add(slot);
                        }

                        if (newSlots.Count > 0)
                        {
                            this.settings.Skills = newSlots;
                            this.saveSettingsCallback();
                        }
                    }

                    await this.SendJson(ctx, new { success = true, skills = this.settings.Skills });
                    return;
                }

                if (path == "/api/dump" && method == "POST")
                {
                    string msg = this.dumpCallback?.Invoke() ?? "No dump callback registered";
                    await this.SendJson(ctx, new { success = true, message = msg });
                    return;
                }

                if (path == "/api/dumps" && method == "GET")
                {
                    await StaticFileHandler.ServeDumpsList(ctx, async (data) => await this.SendJson(ctx, data));
                    return;
                }

                if (path.StartsWith("/api/dumps/") && method == "GET")
                {
                    string fileName = path.Substring("/api/dumps/".Length);
                    await StaticFileHandler.ServeDumpFile(ctx, fileName);
                    return;
                }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    byte[] err = Encoding.UTF8.GetBytes(ex.Message);
                    await ctx.Response.OutputStream.WriteAsync(err);
                    ctx.Response.Close();
                }
                catch { }
            }
        }

        private async Task HandleWebSocket(HttpListenerContext ctx)
        {
            try
            {
                var wsContext = await ctx.AcceptWebSocketAsync(null);
                var ws = wsContext.WebSocket;

                lock (this.wsLock)
                {
                    this.wsClients.Add(ws);
                }

                var buf = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        private async Task BroadcastLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token);

                    List<WebSocket> clients;
                    lock (this.wsLock)
                    {
                        this.wsClients.RemoveAll(c => c.State != WebSocketState.Open);
                        clients = new List<WebSocket>(this.wsClients);
                    }

                    if (clients.Count == 0) continue;

                    var snap = this.statusProvider();
                    byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snap, JsonSettings));

                    foreach (var client in clients)
                    {
                        try
                        {
                            await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token);
                        }
                        catch { }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch { }
            }
        }

        private async Task SendJson(HttpListenerContext ctx, object obj)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, JsonSettings));
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>
    /// Status payload broadcasted to dashboard.
    /// </summary>
    public class AutoExileStatusSnapshot
    {
        public bool IsRunning { get; set; }
        public string Mode { get; set; } = "MapFarm";
        public string State { get; set; } = "Idle";
        public string AreaName { get; set; } = "Unknown";
        public float ExplorationCoverage { get; set; }
        public int HostileCount { get; set; }
        public int PlayerHpPercent { get; set; } = 100;
        public int PlayerEsPercent { get; set; } = 0;
        public int PlayerHpCurrent { get; set; } = 0;
        public int PlayerHpTotal { get; set; } = 0;
        public int PlayerEsCurrent { get; set; } = 0;
        public int PlayerEsTotal { get; set; } = 0;
        public int PlayerManaPercent { get; set; } = 100;
        public string CurrentAction { get; set; } = "Ready";
        public string ActiveDuration { get; set; } = "00:00:00";
        public List<DetectedSkillInfo> DetectedSkills { get; set; } = new();
        public List<DetectedSkillInfo> P2DetectedSkills { get; set; } = new();
        public string LeaderPlayerName { get; set; } = string.Empty;
        public string FollowerPlayerName { get; set; } = string.Empty;
        public List<string> NearbyPlayerNames { get; set; } = new();
        public List<NearbyPlayerDetail> NearbyPlayers { get; set; } = new();
        public List<ActiveBuffInfo> DetectedBuffs { get; set; } = new();
        public List<ActiveBuffInfo> DetectedDebuffs { get; set; } = new();
        public int ActiveTotems { get; set; }
        public int ActiveMinions { get; set; }
        public List<string> ActiveTotemNames { get; set; } = new();
        public List<DeployedObjectInfo> DeployedObjects { get; set; } = new();
        public int FollowerHpPercent { get; set; } = 100;
        public int FollowerEsPercent { get; set; } = 0;
        public int FollowerHpCurrent { get; set; } = 0;
        public int FollowerHpTotal { get; set; } = 0;
        public int FollowerEsCurrent { get; set; } = 0;
        public int FollowerEsTotal { get; set; } = 0;
        public int FollowerManaPercent { get; set; } = 100;
        public bool IsFollowerActive { get; set; } = false;
        public int FollowerSlotIndex { get; set; } = -1;
        public int LeaderSlotIndex { get; set; } = -1;
    }

    public class DetectedSkillInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int CooldownMs { get; set; }
        public bool CanBeUsed { get; set; }
        public int MaxUses { get; set; } = 1;
        public int ActiveCooldowns { get; set; } = 0;
    }

    public class DeployedObjectInfo
    {
        public int TypeId { get; set; }
        public int Count { get; set; }
        public string Category { get; set; } = string.Empty;
    }

    public class ActiveBuffInfo
    {
        public string Name { get; set; } = string.Empty;
        public float TimeLeft { get; set; }
        public int Charges { get; set; }
    }

    public class NearbyPlayerDetail
    {
        public string Name { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public float Distance { get; set; }
        public int HpPercent { get; set; } = 100;
    }
}

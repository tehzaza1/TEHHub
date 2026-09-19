// <copyright file="OffsetHelperV2.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using Coroutine;
    using CoroutineEvents;
    using ImGuiNET;
    using TEHhub.Utils;

    /// <summary>
    /// ImGui interface for OffsetHelper V2 Practical Diagnostics.
    /// Preserves Legacy OffsetHelper completely untouched and serves as an explicit
    /// dev/test entry point for verifying real-game system offsets.
    /// Strictly read-only; no automatic offset mutation or auto-apply.
    /// </summary>
    public static class OffsetHelperV2
    {
        private static readonly Vector4 ColorGreen = new(0.40f, 0.90f, 0.40f, 1f);
        private static readonly Vector4 ColorRed = new(1.00f, 0.40f, 0.40f, 1f);
        private static readonly Vector4 ColorYellow = new(1.00f, 0.85f, 0.40f, 1f);
        private static readonly Vector4 ColorGrey = new(0.60f, 0.60f, 0.60f, 1f);
        private static readonly Vector4 ColorBlue = new(0.40f, 0.75f, 1.00f, 1f);

        private static OffsetHelperV2Engine.V2PracticalReport? latestReport;
        private const double SessionEvidenceSampleIntervalSeconds = 0.10;

        private static bool autoRefresh = false;
        private static DateTime lastAutoRefresh = DateTime.MinValue;
        private static DateTime lastSessionEvidenceSample = DateTime.MinValue;
        private static string statusMessage = "Ready. Press 'Run Diagnostics' to probe active game structures.";

        /// <summary>
        /// Registers the V2 render coroutine with the engine.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(OffsetHelperV2CoRoutine(), priority: UiRenderPriority.CoreWindows);
        }

        private static IEnumerator<Wait> OffsetHelperV2CoRoutine()
        {
            var initialSize = new Vector2(720, 520);
            while (true)
            {
                yield return new Wait(TEHhubEvents.OnRender);

                if (!Core.GHSettings.ShowOffsetHelperV2)
                {
                    continue;
                }

                var nowUtc = DateTime.UtcNow;

                // Lightweight transition evidence is sampled at ~10 Hz while OH2 is visible.
                // Full diagnostics remain manual / 1-second auto-refresh.
                if ((nowUtc - lastSessionEvidenceSample).TotalSeconds >= SessionEvidenceSampleIntervalSeconds)
                {
                    lastSessionEvidenceSample = nowUtc;
                    OffsetHelperV2Engine.SampleSessionEvidence();
                }

                // Handle full-diagnostic auto-refresh (1.0s interval).
                if (autoRefresh && (nowUtc - lastAutoRefresh).TotalSeconds >= 1.0)
                {
                    lastAutoRefresh = nowUtc;
                    latestReport = OffsetHelperV2Engine.RunPracticalDiagnostics();
                }

                ImGui.SetNextWindowSize(initialSize, ImGuiCond.FirstUseEver);
                if (ImGui.Begin("OffsetHelper V2 — Practical Diagnostics (Dev/Test)###OffsetHelperV2Window", ref Core.GHSettings.ShowOffsetHelperV2))
                {
                    DrawHeaderBar();
                    ImGui.Separator();

                    if (latestReport != null)
                    {
                        DrawSummaryCard(latestReport);
                        ImGui.Separator();
                        DrawProbesList(latestReport);
                    }
                    else
                    {
                        ImGui.TextColored(ColorGrey, "No diagnostic report captured yet.");
                        ImGui.TextWrapped("Click 'Run Diagnostics' above to inspect live game structures (Area, Player, WorldData +0x98 Union, Camera, ServerData, Inventories).");
                    }
                }

                ImGui.End();
            }
        }

        private static void DrawHeaderBar()
        {
            if (ImGui.Button("Run Diagnostics (Live)"))
            {
                latestReport = OffsetHelperV2Engine.RunPracticalDiagnostics();
                statusMessage = $"Diagnostics executed at {DateTime.Now:HH:mm:ss}.";
            }

            ImGui.SameLine();
            ImGui.Checkbox("Auto-refresh (1s)", ref autoRefresh);

            ImGui.SameLine();
            if (ImGui.Button("Reset Session Evidence"))
            {
                OffsetHelperV2Engine.ResetSessionEvidence();
                latestReport = OffsetHelperV2Engine.RunPracticalDiagnostics();
                lastAutoRefresh = DateTime.UtcNow;
                lastSessionEvidenceSample = lastAutoRefresh;
                statusMessage = "Session transition evidence reset; lightweight sampler is active while OH2 is open.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Export JSON Report") && latestReport != null)
            {
                ExportReportJson(latestReport);
            }

            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"| State: {Core.States.GameCurrentState}");

            if (!string.IsNullOrEmpty(statusMessage))
            {
                ImGui.TextColored(ColorBlue, statusMessage);
            }
        }

        private static void DrawSummaryCard(OffsetHelperV2Engine.V2PracticalReport report)
        {
            ImGui.Text($"Process: {report.ProcessInfo} | Timestamp: {report.TimestampUtc.ToLocalTime():HH:mm:ss}");

            ImGui.SameLine();
            ImGui.TextColored(ColorGreen, $"Pass: {report.TotalPass}");
            ImGui.SameLine();
            ImGui.TextColored(ColorYellow, $"Warning: {report.TotalWarning}");
            ImGui.SameLine();
            ImGui.TextColored(ColorRed, $"Fail: {report.TotalFail}");
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"Unavailable: {report.TotalUnavailable}");
            ImGui.SameLine();
            ImGui.TextColored(ColorBlue, $"Runtime: {report.ElapsedMilliseconds:F2} ms");

            var evidence = report.SessionEvidence;
            var evidenceColor = evidence.HasCompleteLoadingCycle && evidence.HasAreaTransition
                ? ColorGreen
                : ColorGrey;
            ImGui.TextColored(
                evidenceColor,
                $"Session Evidence: PID={evidence.ProcessId}, Base=0x{evidence.ProcessBase:X}, " +
                $"Samples={evidence.Samples}, LoadingSamples={evidence.LoadingSamples}, " +
                $"IdleSeen={evidence.SawLoadingIdle}, ActiveSeen={evidence.SawLoadingActive}, " +
                $"Load Enter/Exit={evidence.LoadingEnterTransitions}/{evidence.LoadingExitTransitions}, " +
                $"Area Ptr/Hash Changes={evidence.AreaInstanceChanges}/{evidence.AreaHashChanges}");
            ImGui.TextColored(ColorGrey, "Transition sampler: ~10 Hz while this OH2 window is open; full diagnostics remain manual/1s.");
        }

        private static void DrawProbesList(OffsetHelperV2Engine.V2PracticalReport report)
        {
            for (int i = 0; i < report.Probes.Count; i++)
            {
                var probe = report.Probes[i];
                var statusColor = GetStatusColor(probe.Status);
                var statusText = $"[{probe.Status}]";

                ImGui.PushID(i);
                if (ImGui.CollapsingHeader($"{statusText} {probe.Name} — {probe.Summary}###Probe_{i}"))
                {
                    ImGui.Indent();
                    ImGui.TextColored(statusColor, $"Status: {probe.Status}");
                    ImGui.Text($"Summary: {probe.Summary}");

                    if (probe.Details != null && probe.Details.Count > 0)
                    {
                        if (ImGui.TreeNode($"Details ({probe.Details.Count} fields)###Details_{i}"))
                        {
                            foreach (var kvp in probe.Details)
                            {
                                ImGui.TextColored(ColorBlue, $"{kvp.Key}:");
                                ImGui.SameLine();
                                ImGui.TextUnformatted(kvp.Value);
                            }

                            ImGui.TreePop();
                        }
                    }

                    ImGui.Unindent();
                }

                ImGui.PopID();
            }
        }

        private static Vector4 GetStatusColor(OffsetHelperV2Engine.V2ProbeStatus status) => status switch
        {
            OffsetHelperV2Engine.V2ProbeStatus.Pass => ColorGreen,
            OffsetHelperV2Engine.V2ProbeStatus.Warning => ColorYellow,
            OffsetHelperV2Engine.V2ProbeStatus.Fail => ColorRed,
            OffsetHelperV2Engine.V2ProbeStatus.Unavailable => ColorGrey,
            _ => ColorGrey
        };

        private static void ExportReportJson(OffsetHelperV2Engine.V2PracticalReport report)
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "configs", "offset-recovery");
                Directory.CreateDirectory(dir);
                var fileName = $"offsethelper_v2_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var filePath = Path.Combine(dir, fileName);
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                statusMessage = $"Report exported to {fileName}";
            }
            catch (Exception ex)
            {
                statusMessage = $"Export failed: {ex.Message}";
            }
        }
    }
}

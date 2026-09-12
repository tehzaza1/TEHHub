// <copyright file="ImGuiTheme.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System.Numerics;
    using ImGuiNET;
    using L = TEHhub.Localization.OverlayLocalization;

    /// <summary>
    ///     Central dark theme for all TEHhub windows.
    /// </summary>
    internal static class ImGuiTheme
    {
        internal static readonly Vector4 Accent = new(0.18f, 0.82f, 0.76f, 1f);
        internal static readonly Vector4 AccentMuted = new(0.12f, 0.42f, 0.42f, 1f);
        internal static readonly Vector4 TextMuted = new(0.57f, 0.64f, 0.68f, 1f);
        internal static readonly Vector4 Success = new(0.39f, 0.82f, 0.48f, 1f);
        internal static readonly Vector4 Danger = new(0.95f, 0.42f, 0.39f, 1f);
        internal static readonly Vector4 SectionBg = new(0.075f, 0.10f, 0.12f, 1f);

        internal static void Apply()
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 10f;
            style.ChildRounding = 8f;
            style.FrameRounding = 7f;
            style.PopupRounding = 8f;
            style.ScrollbarRounding = 8f;
            style.GrabRounding = 7f;
            style.TabRounding = 7f;
            style.WindowPadding = new Vector2(16f, 14f);
            style.FramePadding = new Vector2(10f, 6f);
            style.ItemSpacing = new Vector2(10f, 8f);
            style.ItemInnerSpacing = new Vector2(8f, 5f);
            style.ScrollbarSize = 14f;
            style.IndentSpacing = 18f;

            var colors = style.Colors;
            colors[(int)ImGuiCol.Text] = new Vector4(0.88f, 0.94f, 0.93f, 1f);
            colors[(int)ImGuiCol.TextDisabled] = TextMuted;
            colors[(int)ImGuiCol.WindowBg] = new Vector4(0.035f, 0.052f, 0.063f, 0.98f);
            colors[(int)ImGuiCol.ChildBg] = new Vector4(0.055f, 0.075f, 0.088f, 1f);
            colors[(int)ImGuiCol.PopupBg] = new Vector4(0.05f, 0.07f, 0.084f, 0.99f);
            colors[(int)ImGuiCol.Border] = new Vector4(0.17f, 0.40f, 0.39f, 0.52f);
            colors[(int)ImGuiCol.FrameBg] = new Vector4(0.09f, 0.13f, 0.15f, 1f);
            colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.12f, 0.20f, 0.22f, 1f);
            colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.13f, 0.28f, 0.28f, 1f);
            colors[(int)ImGuiCol.TitleBg] = new Vector4(0.025f, 0.04f, 0.05f, 1f);
            colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.06f, 0.13f, 0.14f, 1f);
            colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.045f, 0.07f, 0.08f, 1f);
            colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.035f, 0.052f, 0.063f, 0.7f);
            colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.16f, 0.27f, 0.29f, 1f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.21f, 0.40f, 0.40f, 1f);
            colors[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
            colors[(int)ImGuiCol.CheckMark] = Accent;
            colors[(int)ImGuiCol.SliderGrab] = AccentMuted;
            colors[(int)ImGuiCol.SliderGrabActive] = Accent;
            colors[(int)ImGuiCol.Button] = new Vector4(0.10f, 0.22f, 0.23f, 1f);
            colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.14f, 0.36f, 0.36f, 1f);
            colors[(int)ImGuiCol.ButtonActive] = AccentMuted;
            colors[(int)ImGuiCol.Header] = new Vector4(0.09f, 0.19f, 0.21f, 1f);
            colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.13f, 0.33f, 0.34f, 1f);
            colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.15f, 0.47f, 0.45f, 1f);
            colors[(int)ImGuiCol.Separator] = new Vector4(0.16f, 0.36f, 0.36f, 0.6f);
            colors[(int)ImGuiCol.Tab] = new Vector4(0.07f, 0.12f, 0.14f, 1f);
            colors[(int)ImGuiCol.TabHovered] = new Vector4(0.13f, 0.34f, 0.34f, 1f);
            colors[(int)ImGuiCol.TabSelected] = new Vector4(0.10f, 0.25f, 0.27f, 1f);
            colors[(int)ImGuiCol.TabSelectedOverline] = Accent;
        }

        internal static void SectionHeader(string title, string? subtitle = null)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Accent);
            ImGui.TextWrapped(title);
            ImGui.PopStyleColor();
            if (!string.IsNullOrEmpty(subtitle))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                ImGui.TextWrapped(subtitle);
                ImGui.PopStyleColor();
            }

            ImGui.Separator();
            ImGui.Spacing();
        }

        internal static void DrawBrandHeader(string version, int activePlugins, int totalPlugins)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.13f, 0.14f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
            ImGui.BeginChild("TEHhubBrandHeader", Vector2.Zero, ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.PushStyleColor(ImGuiCol.Text, Accent);
            ImGui.SetWindowFontScale(1.35f);
            ImGui.TextUnformatted("TEHhub");
            ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();
            var centerLabel = L.F("settings.brand.center", "CONTROL CENTER · {0}", version);
            if (ImGui.GetContentRegionAvail().X > ImGui.CalcTextSize(centerLabel).X + ImGui.GetStyle().ItemSpacing.X)
            {
                ImGui.SameLine();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
            ImGui.TextWrapped(centerLabel);
            ImGui.TextWrapped(L.F("settings.brand.plugins", "{0}/{1} plugins active · Local overlay workspace", activePlugins, totalPlugins));
            ImGui.PopStyleColor();
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        internal static void BeginPanel(string id)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, SectionBg);
            ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.Borders);
        }

        internal static void EndPanel()
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
    }
}

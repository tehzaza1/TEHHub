namespace TEHhub.OffsetDoctor.Watch;

public sealed class WatchTargetInfo
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required string UserActionHint { get; init; }

    public static readonly IReadOnlyList<WatchTargetInfo> DefaultTargets =
    [
        new WatchTargetInfo
        {
            NodeId = "ui_left_panel",
            DisplayName = "LeftPanelPtr",
            Category = "UI Elements",
            UserActionHint = "Open a left/right UI panel such as inventory, character, stash, or another side panel."
        },
        new WatchTargetInfo
        {
            NodeId = "ui_right_panel",
            DisplayName = "RightPanelPtr",
            Category = "UI Elements",
            UserActionHint = "Open a left/right UI panel such as inventory, character, stash, or another side panel."
        },
        new WatchTargetInfo
        {
            NodeId = "ui_passive_tree_panel",
            DisplayName = "PassiveSkillTreePanel",
            Category = "UI Elements",
            UserActionHint = "Open the passive skill tree."
        },
        new WatchTargetInfo
        {
            NodeId = "ui_map_parent",
            DisplayName = "MapParentPtr",
            Category = "UI Elements",
            UserActionHint = "Open the map or world map."
        },
        new WatchTargetInfo
        {
            NodeId = "ui_world_map_panel",
            DisplayName = "WorldMapPanelPtr",
            Category = "UI Elements",
            UserActionHint = "Open the map or world map."
        },
        new WatchTargetInfo
        {
            NodeId = "loading_state_area_details",
            DisplayName = "AreaLoadingState.CurrentAreaDetailsPtr",
            Category = "Area Loading State",
            UserActionHint = "Change area or enter a loading transition."
        },
        new WatchTargetInfo
        {
            NodeId = "comp_buffs_status_effects",
            DisplayName = "Buffs.StatusEffectPtr",
            Category = "Player & Components",
            UserActionHint = "Use a flask, gain a buff, or wait while active buffs are present."
        }
    ];

    public static List<WatchTargetInfo> ResolveTargets(string? targetFilter)
    {
        if (string.IsNullOrWhiteSpace(targetFilter) || targetFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return [.. DefaultTargets];
        }

        var filter = targetFilter.Trim();

        if (filter.Equals("ui", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTargets.Where(t => t.Category == "UI Elements").ToList();
        }

        if (filter.Equals("loading", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTargets.Where(t => t.Category == "Area Loading State").ToList();
        }

        if (filter.Equals("buffs", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTargets.Where(t => t.Category == "Player & Components" || t.NodeId.Contains("buff", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Direct matching by NodeId or DisplayName
        var matched = DefaultTargets.Where(t =>
            t.NodeId.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
            t.DisplayName.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
            t.NodeId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            t.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matched.Count > 0)
        {
            return matched;
        }

        // Fallback to all if unrecognized
        return [.. DefaultTargets];
    }
}

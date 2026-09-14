using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExpeditionPlanner;

Console.WriteLine("=== Running ExpeditionPlanner Route Solver Tests ===");

int failed = 0;
var settings = new ExpeditionPlannerSettings
{
    MaxExplosiveBudget = 4,
    MaxPlacementRangeGrid = 55f,
    BlastRadiusGrid = 28f,
    BombClearanceRadiusGrid = 4.5f,
    CampExclusionRadiusGrid = 30f
};

var detonatorGrid = new Vector3(100f, 100f, 0f);
var detonatorWorld = new Vector3(1000f, 1000f, 0f);

// Test 1: Simple 3 pillars within range of detonator.
// Pillar 1: Opulent (2 slots)
// Pillar 2: Regular (1 slot)
// Pillar 3: Final Stack Pillar (4 slots)
{
    Console.WriteLine("\nTest 1: 3 pillars with 4 budget (All reachable)");
    var targets = new List<ExpeditionTarget>
    {
        new() { EntityId = 1, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 1", AnchorRuneName = "Opulent", HoleCount = 2, GridPosition = new Vector3(140f, 100f, 0f) },
        new() { EntityId = 2, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 2", AnchorRuneName = "Minor", HoleCount = 1, GridPosition = new Vector3(175f, 100f, 0f) },
        new() { EntityId = 3, Kind = TargetKind.RemnantPillar, DisplayName = "Final Pillar", AnchorRuneName = "Minor", HoleCount = 4, GridPosition = new Vector3(210f, 100f, 0f) }
    };

    var route = ExpeditionGlobalRoutePlanner.Solve(detonatorGrid, detonatorWorld, new List<PlacedBombInfo>(), targets, null, settings);
    Console.WriteLine($"Placements count: {route.Placements.Count}");
    foreach (var p in route.Placements)
    {
        Console.WriteLine($"  Step {p.Step} at ({p.GridPosition.X:F0}, {p.GridPosition.Y:F0}): Hits {string.Join(", ", p.CoveredTargets.Select(t => t.DisplayName))}");
    }
    foreach (var w in route.Warnings) Console.WriteLine($"  Warning: {w}");
    var covered = route.Placements.SelectMany(p => p.CoveredTargets).Select(t => t.EntityId).Distinct().Count();
    Console.WriteLine($"Covered pillars: {covered}/3");
    if (covered != 3) { Console.WriteLine("FAILED: Expected 3 covered pillars"); failed++; }
    else Console.WriteLine("PASSED: Covered all 3 pillars");
}

// Test 2: 5 pillars with 3 budget (More pillars than budget).
// Pillar 1: (1 slot)
// Pillar 2: (1 slot)
// Pillar 3: (1 slot)
// Pillar 4: (1 slot)
// Pillar 5: Final Stack Pillar (5 slots)
{
    Console.WriteLine("\nTest 2: 5 pillars with 3 budget (Pillars > Budget)");
    var targets = new List<ExpeditionTarget>
    {
        new() { EntityId = 1, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 1", HoleCount = 1, GridPosition = new Vector3(140f, 100f, 0f) },
        new() { EntityId = 2, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 2", HoleCount = 1, GridPosition = new Vector3(175f, 100f, 0f) },
        new() { EntityId = 3, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 3", HoleCount = 1, GridPosition = new Vector3(140f, 140f, 0f) },
        new() { EntityId = 4, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 4", HoleCount = 1, GridPosition = new Vector3(140f, 60f, 0f) },
        new() { EntityId = 5, Kind = TargetKind.RemnantPillar, DisplayName = "Final Pillar", HoleCount = 5, GridPosition = new Vector3(210f, 100f, 0f) }
    };
    var smallSettings = new ExpeditionPlannerSettings
    {
        MaxExplosiveBudget = 3,
        MaxPlacementRangeGrid = 55f,
        BlastRadiusGrid = 28f,
        BombClearanceRadiusGrid = 4.5f,
        CampExclusionRadiusGrid = 30f
    };

    var route = ExpeditionGlobalRoutePlanner.Solve(detonatorGrid, detonatorWorld, new List<PlacedBombInfo>(), targets, null, smallSettings);
    Console.WriteLine($"Placements count: {route.Placements.Count}");
    foreach (var p in route.Placements)
    {
        Console.WriteLine($"  Step {p.Step} at ({p.GridPosition.X:F0}, {p.GridPosition.Y:F0}): Hits {string.Join(", ", p.CoveredTargets.Select(t => t.DisplayName))}");
    }
    foreach (var w in route.Warnings) Console.WriteLine($"  Warning: {w}");
    bool hitsFinal = route.Placements.Any(p => p.CoveredTargets.Any(t => t.EntityId == 5));
    Console.WriteLine($"Hits final pillar (5 slots): {hitsFinal}");
    if (!hitsFinal) { Console.WriteLine("FAILED: Final pillar was not reached!"); failed++; }
    else Console.WriteLine("PASSED: Final pillar was reached!");
}

// Test 3: Distant pillar requiring bridge bomb.
// Detonator at (100, 100).
// Pillar 1 at (135, 100) (1 slot)
// Final Pillar at (230, 100) (4 slots) - requires a bridge bomb between (135, 100) and (230, 100).
// Pillar 3 at (60, 100) (unreachable/opposite side)
{
    Console.WriteLine("\nTest 3: Remote Final Pillar requiring bridge bomb");
    var targets = new List<ExpeditionTarget>
    {
        new() { EntityId = 1, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 1", HoleCount = 1, GridPosition = new Vector3(135f, 100f, 0f) },
        new() { EntityId = 2, Kind = TargetKind.RemnantPillar, DisplayName = "Final Pillar", HoleCount = 4, GridPosition = new Vector3(230f, 100f, 0f) },
        new() { EntityId = 3, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 3 (Opposite)", HoleCount = 1, GridPosition = new Vector3(40f, 100f, 0f) }
    };
    var budget3Settings = new ExpeditionPlannerSettings
    {
        MaxExplosiveBudget = 3,
        MaxPlacementRangeGrid = 50f,
        BlastRadiusGrid = 25f,
        BombClearanceRadiusGrid = 4.5f,
        CampExclusionRadiusGrid = 15f
    };

    var route = ExpeditionGlobalRoutePlanner.Solve(detonatorGrid, detonatorWorld, new List<PlacedBombInfo>(), targets, null, budget3Settings);
    Console.WriteLine($"Placements count: {route.Placements.Count}");
    foreach (var p in route.Placements)
    {
        Console.WriteLine($"  Step {p.Step} at ({p.GridPosition.X:F0}, {p.GridPosition.Y:F0}): Hits {string.Join(", ", p.CoveredTargets.Select(t => t.DisplayName))}");
    }
    foreach (var w in route.Warnings) Console.WriteLine($"  Warning: {w}");
    bool hitsFinal = route.Placements.Any(p => p.CoveredTargets.Any(t => t.EntityId == 2));
    Console.WriteLine($"Hits final pillar: {hitsFinal}");
    if (!hitsFinal) { Console.WriteLine("FAILED: Final pillar was not reached!"); failed++; }
    else Console.WriteLine("PASSED: Final pillar was reached!");
}

// Test 4: Opulent rune priority.
// Detonator at (100, 100).
// Pillar 1 at (130, 80) (Regular 2 slots)
// Pillar 2 at (130, 120) (Opulent 2 slots)
// Pillar 3 at (170, 100) (Final Pillar 3 slots)
{
    Console.WriteLine("\nTest 4: Opulent rune priority (Opulent should be hit in step 1)");
    var targets = new List<ExpeditionTarget>
    {
        new() { EntityId = 1, Kind = TargetKind.RemnantPillar, DisplayName = "Regular Pillar", AnchorRuneName = "Minor", HoleCount = 2, GridPosition = new Vector3(130f, 75f, 0f) },
        new() { EntityId = 2, Kind = TargetKind.RemnantPillar, DisplayName = "Opulent Pillar", AnchorRuneName = "Opulent", HoleCount = 2, GridPosition = new Vector3(130f, 125f, 0f) },
        new() { EntityId = 3, Kind = TargetKind.RemnantPillar, DisplayName = "Final Pillar", AnchorRuneName = "Minor", HoleCount = 4, GridPosition = new Vector3(175f, 100f, 0f) }
    };
    var route = ExpeditionGlobalRoutePlanner.Solve(detonatorGrid, detonatorWorld, new List<PlacedBombInfo>(), targets, null, settings);
    Console.WriteLine($"Placements count: {route.Placements.Count}");
    foreach (var p in route.Placements)
    {
        Console.WriteLine($"  Step {p.Step} at ({p.GridPosition.X:F0}, {p.GridPosition.Y:F0}): Hits {string.Join(", ", p.CoveredTargets.Select(t => t.DisplayName))}");
    }
    bool step1HasOpulent = route.Placements.Count > 0 && route.Placements[0].CoveredTargets.Any(t => t.AnchorRuneName == "Opulent");
    Console.WriteLine($"Step 1 hits Opulent: {step1HasOpulent}");
    if (!step1HasOpulent) { Console.WriteLine("FAILED: Opulent was not hit in Step 1!"); failed++; }
    else Console.WriteLine("PASSED: Opulent prioritized in Step 1!");
}

// Test 5: Final pillar is completely unreachable (exceeds budget), bestPrefix fallback should prevail.
{
    Console.WriteLine("\nTest 5: Unreachable Final Pillar (Exceeds budget, fallback to best prefix)");
    var targets = new List<ExpeditionTarget>
    {
        new() { EntityId = 1, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 1", HoleCount = 1, GridPosition = new Vector3(135f, 100f, 0f) },
        new() { EntityId = 2, Kind = TargetKind.RemnantPillar, DisplayName = "Pillar 2", HoleCount = 1, GridPosition = new Vector3(170f, 100f, 0f) },
        new() { EntityId = 3, Kind = TargetKind.RemnantPillar, DisplayName = "Final Pillar (1000 range)", HoleCount = 5, GridPosition = new Vector3(1000f, 1000f, 0f) }
    };
    var smallBudget = new ExpeditionPlannerSettings
    {
        MaxExplosiveBudget = 2,
        MaxPlacementRangeGrid = 50f,
        BlastRadiusGrid = 25f,
        BombClearanceRadiusGrid = 4.5f,
        CampExclusionRadiusGrid = 15f
    };
    var route = ExpeditionGlobalRoutePlanner.Solve(detonatorGrid, detonatorWorld, new List<PlacedBombInfo>(), targets, null, smallBudget);
    Console.WriteLine($"Placements count: {route.Placements.Count}");
    foreach (var p in route.Placements)
    {
        Console.WriteLine($"  Step {p.Step} at ({p.GridPosition.X:F0}, {p.GridPosition.Y:F0}): Hits {string.Join(", ", p.CoveredTargets.Select(t => t.DisplayName))}");
    }
    foreach (var w in route.Warnings) Console.WriteLine($"  Warning: {w}");
    var covered = route.Placements.SelectMany(p => p.CoveredTargets).Select(t => t.EntityId).Distinct().Count();
    Console.WriteLine($"Covered reachable pillars: {covered}/2");
    if (covered != 2) { Console.WriteLine("FAILED: Expected 2 covered reachable pillars"); failed++; }
    else Console.WriteLine("PASSED: Successfully covered reachable pillars despite unreachable final pillar!");
}

Console.WriteLine($"\n=== Tests Completed (Failures: {failed}) ===");
if (failed > 0) Environment.Exit(1);

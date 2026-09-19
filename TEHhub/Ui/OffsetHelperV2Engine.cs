// <copyright file="OffsetHelperV2Engine.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Objects.States;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Shared;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.Utils;

    /// <summary>
    /// Practical diagnostics engine for OffsetHelper V2.
    /// Performs targeted, read-only verification of real game systems:
    /// Area/Level/Hash, LoadingState, LocalPlayer, AwakeEntities,
    /// WorldData +0x98 Union Audit, Camera/WorldToScreen, ServerData,
    /// PlayerInventories, Inventory/Stash item sampling, and Component chains.
    /// 
    /// SAFETY GUARANTEES:
    /// - Strictly read-only: zero game-memory writes.
    /// - No automatic offset mutation or auto-apply.
    /// - Preserves Legacy OffsetHelper completely untouched.
    /// </summary>
    public static class OffsetHelperV2Engine
    {
        private static readonly int AreaPlayerInfoOffset =
            Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.PlayerInfo)).ToInt32();
        private static readonly int AreaEntitiesOffset =
            Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.Entities)).ToInt32();
        private static readonly int WorldAreaDetailsOffset =
            Marshal.OffsetOf<WorldDataOffset>(nameof(WorldDataOffset.WorldAreaDetailsPtr)).ToInt32();
        private static readonly int CameraStructureOffset =
            Marshal.OffsetOf<WorldDataOffset>(nameof(WorldDataOffset.CameraStructurePtr)).ToInt32();
        private static readonly int CameraMatrixOffset =
            Marshal.OffsetOf<CameraStructure>(nameof(CameraStructure.WorldToScreenMatrix)).ToInt32();
        private static readonly int EffectiveCameraMatrixOffset = CameraStructureOffset + CameraMatrixOffset;

        private static readonly object SessionEvidenceGate = new();
        private static int sessionSamples;
        private static int sessionLoadingSamples;
        private static bool sessionSawLoadingIdle;
        private static bool sessionSawLoadingActive;
        private static int sessionLoadingEnterTransitions;
        private static int sessionLoadingExitTransitions;
        private static int sessionAreaInstanceChanges;
        private static int sessionAreaHashChanges;
        private static int sessionLocalPlayerChanges;
        private static int sessionWorldDataChanges;
        private static DateTime? sessionFirstSampleUtc;
        private static DateTime? sessionLastSampleUtc;
        private static byte? previousLoadingState;
        private static long previousAreaInstance;
        private static uint previousAreaHash;
        private static long previousLocalPlayer;
        private static long previousWorldData;

        public enum V2ProbeStatus
        {
            Pass,
            Warning,
            Fail,
            Unavailable
        }

        public sealed record V2ProbeResult(
            string Name,
            V2ProbeStatus Status,
            string Summary,
            Dictionary<string, string> Details,
            long MeasuredValue = 0);

        public sealed record V2SessionEvidence(
            int Samples,
            int LoadingSamples,
            bool SawLoadingIdle,
            bool SawLoadingActive,
            int LoadingEnterTransitions,
            int LoadingExitTransitions,
            int AreaInstanceChanges,
            int AreaHashChanges,
            int LocalPlayerChanges,
            int WorldDataChanges,
            DateTime? FirstSampleUtc,
            DateTime? LastSampleUtc)
        {
            public bool HasCompleteLoadingCycle => this.LoadingEnterTransitions > 0 && this.LoadingExitTransitions > 0;

            public bool HasAreaTransition => this.AreaInstanceChanges > 0 || this.AreaHashChanges > 0;
        }

        public sealed record V2PracticalReport(
            DateTime TimestampUtc,
            string GameState,
            string ProcessInfo,
            List<V2ProbeResult> Probes,
            int TotalPass,
            int TotalWarning,
            int TotalFail,
            int TotalUnavailable,
            double ElapsedMilliseconds,
            V2SessionEvidence SessionEvidence);

        internal static void ResetSessionEvidence()
        {
            lock (SessionEvidenceGate)
            {
                sessionSamples = 0;
                sessionLoadingSamples = 0;
                sessionSawLoadingIdle = false;
                sessionSawLoadingActive = false;
                sessionLoadingEnterTransitions = 0;
                sessionLoadingExitTransitions = 0;
                sessionAreaInstanceChanges = 0;
                sessionAreaHashChanges = 0;
                sessionLocalPlayerChanges = 0;
                sessionWorldDataChanges = 0;
                sessionFirstSampleUtc = null;
                sessionLastSampleUtc = null;
                previousLoadingState = null;
                previousAreaInstance = 0;
                previousAreaHash = 0;
                previousLocalPlayer = 0;
                previousWorldData = 0;
            }
        }

        internal static V2SessionEvidence GetSessionEvidence()
        {
            lock (SessionEvidenceGate)
            {
                return new V2SessionEvidence(
                    sessionSamples,
                    sessionLoadingSamples,
                    sessionSawLoadingIdle,
                    sessionSawLoadingActive,
                    sessionLoadingEnterTransitions,
                    sessionLoadingExitTransitions,
                    sessionAreaInstanceChanges,
                    sessionAreaHashChanges,
                    sessionLocalPlayerChanges,
                    sessionWorldDataChanges,
                    sessionFirstSampleUtc,
                    sessionLastSampleUtc);
            }
        }

        internal static void RecordSessionEvidenceSample(
            byte? isLoading,
            long areaInstance,
            uint areaHash,
            long localPlayer,
            long worldData,
            DateTime timestampUtc)
        {
            lock (SessionEvidenceGate)
            {
                sessionSamples++;
                sessionFirstSampleUtc ??= timestampUtc;
                sessionLastSampleUtc = timestampUtc;

                if (isLoading is 0 or 1)
                {
                    sessionLoadingSamples++;
                    if (isLoading == 0)
                    {
                        sessionSawLoadingIdle = true;
                    }
                    else
                    {
                        sessionSawLoadingActive = true;
                    }

                    if (previousLoadingState.HasValue && previousLoadingState.Value != isLoading.Value)
                    {
                        if (previousLoadingState.Value == 0 && isLoading.Value == 1)
                        {
                            sessionLoadingEnterTransitions++;
                        }
                        else if (previousLoadingState.Value == 1 && isLoading.Value == 0)
                        {
                            sessionLoadingExitTransitions++;
                        }
                    }

                    previousLoadingState = isLoading.Value;
                }

                if (areaInstance != 0)
                {
                    if (previousAreaInstance != 0 && previousAreaInstance != areaInstance)
                    {
                        sessionAreaInstanceChanges++;
                    }

                    previousAreaInstance = areaInstance;
                }

                if (areaHash != 0)
                {
                    if (previousAreaHash != 0 && previousAreaHash != areaHash)
                    {
                        sessionAreaHashChanges++;
                    }

                    previousAreaHash = areaHash;
                }

                if (localPlayer != 0)
                {
                    if (previousLocalPlayer != 0 && previousLocalPlayer != localPlayer)
                    {
                        sessionLocalPlayerChanges++;
                    }

                    previousLocalPlayer = localPlayer;
                }

                if (worldData != 0)
                {
                    if (previousWorldData != 0 && previousWorldData != worldData)
                    {
                        sessionWorldDataChanges++;
                    }

                    previousWorldData = worldData;
                }
            }
        }

        /// <summary>
        /// Executes all practical diagnostic probes in a safe, read-only manner.
        /// </summary>
        public static V2PracticalReport RunPracticalDiagnostics()
        {
            var startedTimestamp = Stopwatch.GetTimestamp();
            var probes = new List<V2ProbeResult>();
            var gameState = Core.States.GameCurrentState.ToString();
            var procInfo = Core.Process.Information != null
                ? $"{Core.Process.Information.ProcessName} (PID {Core.Process.Information.Id})"
                : "No attached process";

            var reader = Core.Process.Handle;
            if (reader == null || reader.IsInvalid || reader.IsClosed)
            {
                probes.Add(new V2ProbeResult(
                    "Process Attachment",
                    V2ProbeStatus.Unavailable,
                    "Process memory handle is not open or invalid.",
                    new() { ["Attached"] = "false" }));

                return BuildReport(gameState, procInfo, probes, startedTimestamp);
            }

            ObserveSessionEvidence(reader);

            // 1. Area / Area Level / Area Hash Probe
            probes.Add(ProbeAreaInstance(reader));

            // 2. Loading State Probe
            probes.Add(ProbeLoadingState(reader));

            // 3. LocalPlayer Probe
            probes.Add(ProbeLocalPlayer(reader));

            // 4. Awake Entities Probe
            probes.Add(ProbeAwakeEntities(reader));

            // 5. WorldData +0x98 Union Audit Probe
            probes.Add(ProbeWorldDataUnion(reader));

            // 6. Camera / WorldToScreen Projection Probe
            probes.Add(ProbeCameraWorldToScreen(reader));

            // 7. ServerData Probe
            probes.Add(ProbeServerData(reader));

            // 8. PlayerInventories Probe
            probes.Add(ProbePlayerInventories(reader));

            // 9. Player inventory bounded item sampling probe
            probes.Add(ProbeInventoryItemSampling(reader));

            // 10. Stash inventory context probe
            probes.Add(ProbeStashInventoryContext(reader));

            // 11. Entity / Component Validation Probe
            probes.Add(ProbeEntityComponents(reader));

            return BuildReport(gameState, procInfo, probes, startedTimestamp);
        }

        // =====================================================================
        // Probe 1: Area / Level / Hash
        // =====================================================================
        private static V2ProbeResult ProbeAreaInstance(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Unavailable,
                        "InGameState address is null (not in game world).", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                details["AreaInstanceDataPtr"] = $"0x{inGameState.AreaInstanceData.ToInt64():X}";

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Fail,
                        $"AreaInstanceData pointer 0x{inGameState.AreaInstanceData.ToInt64():X} is non-canonical.", details);
                }

                var areaOffsets = reader.ReadMemory<AreaInstanceOffsets>(inGameState.AreaInstanceData);
                details["CurrentAreaLevel"] = areaOffsets.CurrentAreaLevel.ToString();
                details["CurrentAreaHash"] = $"0x{areaOffsets.CurrentAreaHash:X8}";

                var envVec = CanonicalStructuralInvariants.ValidateStdVector(areaOffsets.Environments);
                details["EnvironmentsVector"] = envVec.Detail;

                if (!envVec.IsValid)
                {
                    return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Fail,
                        $"Environments vector failed structural validation: {envVec.Detail}", details, areaOffsets.CurrentAreaLevel);
                }

                if (areaOffsets.CurrentAreaLevel < 1 || areaOffsets.CurrentAreaLevel > 100)
                {
                    return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Warning,
                        $"CurrentAreaLevel ({areaOffsets.CurrentAreaLevel}) outside expected 1-100 range.", details, areaOffsets.CurrentAreaLevel);
                }

                return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Pass,
                    $"Area Level: {areaOffsets.CurrentAreaLevel}, Hash: 0x{areaOffsets.CurrentAreaHash:X8}", details, areaOffsets.CurrentAreaLevel);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Area & Zone Info", V2ProbeStatus.Fail, "Exception during AreaInstance probe.", details);
            }
        }

        // =====================================================================
        // Probe 2: Loading State
        // =====================================================================
        private static V2ProbeResult ProbeLoadingState(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var loadingAddr = Core.States.AreaLoading.Address;
                details["AreaLoadingStateAddr"] = $"0x{loadingAddr.ToInt64():X}";

                if (loadingAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Area Loading State", V2ProbeStatus.Unavailable,
                        "AreaLoadingState address is null.", details);
                }

                var data = reader.ReadMemory<AreaLoadingStateOffset>(loadingAddr);
                details["IsLoading"] = data.IsLoading.ToString();
                details["TotalLoadingScreenTimeMs"] = $"{data.TotalLoadingScreenTimeMs} ms";
                details["CurrentAreaDetailsPtr"] = $"0x{data.CurrentAreaDetailsPtr.ToInt64():X}";

                var isDetailsCanonical = data.CurrentAreaDetailsPtr == IntPtr.Zero ||
                                         CanonicalStructuralInvariants.IsCanonicalPointer(data.CurrentAreaDetailsPtr);
                details["AreaDetailsCanonicalOrNull"] = isDetailsCanonical.ToString();

                if (data.IsLoading != 0 && data.IsLoading != 1)
                {
                    return new V2ProbeResult("Area Loading State", V2ProbeStatus.Warning,
                        $"IsLoading has unexpected value {data.IsLoading} (expected 0 or 1).", details, data.IsLoading);
                }

                if (!isDetailsCanonical)
                {
                    return new V2ProbeResult("Area Loading State", V2ProbeStatus.Warning,
                        $"CurrentAreaDetailsPtr 0x{data.CurrentAreaDetailsPtr.ToInt64():X} is non-null and non-canonical.", details, data.IsLoading);
                }

                return new V2ProbeResult("Area Loading State", V2ProbeStatus.Pass,
                    $"IsLoading: {data.IsLoading}, Time: {data.TotalLoadingScreenTimeMs}ms, DetailsPtr: 0x{data.CurrentAreaDetailsPtr.ToInt64():X}", details, data.IsLoading);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Area Loading State", V2ProbeStatus.Fail, "Exception during LoadingState probe.", details);
            }
        }

        // =====================================================================
        // Probe 3: LocalPlayer
        // =====================================================================
        private static V2ProbeResult ProbeLocalPlayer(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Unavailable, "InGameState is null.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Unavailable, "AreaInstanceData is non-canonical.", details);
                }

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + AreaPlayerInfoOffset);
                details["LocalPlayerPtr"] = $"0x{playerInfo.LocalPlayerPtr.ToInt64():X}";

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(playerInfo.LocalPlayerPtr))
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Warning,
                        $"LocalPlayerPtr 0x{playerInfo.LocalPlayerPtr.ToInt64():X} is null or non-canonical (not in world).", details);
                }

                var entity = reader.ReadMemory<EntityOffsets>(playerInfo.LocalPlayerPtr);
                details["VTablePtr"] = $"0x{entity.ItemBase.VTablePtr.ToInt64():X}";
                details["EntityDetailsPtr"] = $"0x{entity.ItemBase.EntityDetailsPtr.ToInt64():X}";
                details["EntityId"] = entity.Id.ToString();
                details["IsValidByte"] = $"0x{entity.IsValid:X2}";

                var compVec = CanonicalStructuralInvariants.ValidateStdVector(entity.ItemBase.ComponentListPtr);
                details["ComponentListVector"] = compVec.Detail;

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(entity.ItemBase.EntityDetailsPtr))
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Fail,
                        "LocalPlayer EntityDetailsPtr is non-canonical.", details);
                }

                var entDetails = reader.ReadMemory<EntityDetails>(entity.ItemBase.EntityDetailsPtr);
                var path = reader.ReadStdWString(entDetails.name);
                details["MetadataPath"] = path;

                if (!path.StartsWith("Metadata/Characters/", StringComparison.OrdinalIgnoreCase))
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Warning,
                        $"LocalPlayer path '{path}' does not start with 'Metadata/Characters/'.", details);
                }

                var isValid = EntityHelper.IsValidEntity(entity.IsValid);
                details["IsValid"] = isValid.ToString();

                if (!isValid)
                {
                    return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Warning,
                        $"Player entity resolved as '{path}' but validity byte is not accepted.", details, entity.Id);
                }

                return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Pass,
                    $"Player: {path} (Id: {entity.Id}, Valid: {isValid})", details, entity.Id);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("LocalPlayer", V2ProbeStatus.Fail, "Exception during LocalPlayer probe.", details);
            }
        }

        // =====================================================================
        // Probe 4: Awake Entities
        // =====================================================================
        private static V2ProbeResult ProbeAwakeEntities(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Unavailable, "InGameState is null.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Unavailable, "AreaInstanceData is non-canonical.", details);
                }

                // EntityListStruct starts at the canonical AreaInstanceOffsets.Entities field.
                var awakeMap = reader.ReadMemory<StdMap>(inGameState.AreaInstanceData + AreaEntitiesOffset);
                details["AwakeMapHead"] = $"0x{awakeMap.Head.ToInt64():X}";
                details["AwakeMapSize"] = awakeMap.Size.ToString();

                if (awakeMap.Size < 0 || awakeMap.Size > CanonicalVersionedSemantics.MaxMapSize)
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail,
                        $"AwakeEntities StdMap size {awakeMap.Size} exceeds safety bounds [0, {CanonicalVersionedSemantics.MaxMapSize}].", details, awakeMap.Size);
                }

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(awakeMap.Head))
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail,
                        $"AwakeEntities Head pointer 0x{awakeMap.Head.ToInt64():X} is non-canonical.", details);
                }

                if (!reader.TryReadMemory<MapNodeHeader>(awakeMap.Head, out var sentinel))
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail,
                        "AwakeEntities sentinel node could not be read.", details, awakeMap.Size);
                }

                MapNodeHeader? rootNode = null;
                if (awakeMap.Size > 0 && CanonicalStructuralInvariants.IsCanonicalPointer(sentinel.Parent))
                {
                    if (!reader.TryReadMemory<MapNodeHeader>(sentinel.Parent, out var root))
                    {
                        return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail,
                            "AwakeEntities root node could not be read.", details, awakeMap.Size);
                    }

                    rootNode = root;
                }

                var mapValidation = CanonicalStructuralInvariants.ValidateStdMap(
                    awakeMap.Head.ToInt64(),
                    awakeMap.Size,
                    sentinel.IsNil,
                    sentinel.Color,
                    sentinel.Parent.ToInt64(),
                    rootNode.HasValue ? rootNode.Value.IsNil : null,
                    rootNode.HasValue ? rootNode.Value.Color : null);
                details["StdMapValidation"] = mapValidation.Detail;

                if (!mapValidation.IsValid)
                {
                    return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail,
                        $"AwakeEntities StdMap failed sentinel/root validation: {mapValidation.Detail}", details, awakeMap.Size);
                }

                // Sample awake entities from cached instance
                var liveArea = Core.States.InGameStateObject.CurrentAreaInstance;
                var awakeDict = liveArea?.AwakeEntities;
                int sampledCount = 0;
                if (awakeDict != null && !awakeDict.IsEmpty)
                {
                    details["CachedAwakeCount"] = awakeDict.Count.ToString();
                    foreach (var kvp in awakeDict)
                    {
                        if (sampledCount >= 5) break;
                        var ent = kvp.Value;
                        if (ent != null && ent.Address != IntPtr.Zero)
                        {
                            details[$"SampledEntity_{sampledCount}"] = $"Id={ent.Id}, Path={ent.Path}, Addr=0x{ent.Address.ToInt64():X}";
                            sampledCount++;
                        }
                    }
                }
                details["SampledEntitiesCount"] = sampledCount.ToString();

                return new V2ProbeResult("Awake Entities", V2ProbeStatus.Pass,
                    $"StdMap Size: {awakeMap.Size}, Live Cached: {awakeDict?.Count ?? 0}, Sampled: {sampledCount}", details, awakeMap.Size);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Awake Entities", V2ProbeStatus.Fail, "Exception during AwakeEntities probe.", details);
            }
        }

        // =====================================================================
        // Probe 5: WorldData +0x98 Union Audit
        // =====================================================================
        private static V2ProbeResult ProbeWorldDataUnion(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Unavailable, "InGameState is null.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                details["WorldDataPtr"] = $"0x{inGameState.WorldData.ToInt64():X}";

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.WorldData))
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Fail,
                        $"WorldData pointer 0x{inGameState.WorldData.ToInt64():X} is non-canonical.", details);
                }

                // Audit the explicit-layout overlap using canonical struct metadata, not duplicated literals.
                var worldData = reader.ReadMemory<WorldDataOffset>(inGameState.WorldData);
                var worldAreaDetailsPtr = worldData.WorldAreaDetailsPtr;
                var cameraCodePtr = worldData.CameraStructurePtr.CodePtr;
                var matrix = worldData.CameraStructurePtr.WorldToScreenMatrix;

                details["WorldAreaDetailsOffset"] = $"+0x{WorldAreaDetailsOffset:X}";
                details["CameraStructureOffset"] = $"+0x{CameraStructureOffset:X}";
                details["CameraMatrixOffsetWithinStructure"] = $"+0x{CameraMatrixOffset:X}";
                details["EffectiveCameraMatrixOffset"] = $"+0x{EffectiveCameraMatrixOffset:X}";
                details[$"WorldAreaDetailsPtr (+0x{WorldAreaDetailsOffset:X})"] = $"0x{worldAreaDetailsPtr.ToInt64():X}";
                details[$"CameraStructure.CodePtr (+0x{CameraStructureOffset:X})"] =
                    $"0x{cameraCodePtr.ToInt64():X} (shares the explicit-layout start with WorldAreaDetailsPtr)";
                details[$"CameraStructure.Matrix (+0x{EffectiveCameraMatrixOffset:X})"] =
                    $"M11={matrix.M11:0.00}, M22={matrix.M22:0.00}, M33={matrix.M33:0.00}, M44={matrix.M44:0.00}";

                // 4. Trace WorldAreaDetailsStruct dereference:
                bool areaDetailsValid = false;
                if (CanonicalStructuralInvariants.IsCanonicalPointer(worldAreaDetailsPtr))
                {
                    var areaStruct = reader.ReadMemory<WorldAreaDetailsStruct>(worldAreaDetailsPtr);
                    details["WorldAreaDetailsRowPtr (+0x98 of AreaDetails)"] = $"0x{areaStruct.WorldAreaDetailsRowPtr.ToInt64():X}";
                    areaDetailsValid = CanonicalStructuralInvariants.IsCanonicalPointer(areaStruct.WorldAreaDetailsRowPtr);
                    details["WorldAreaDetailsRowValid"] = areaDetailsValid.ToString();
                }

                details["UnionArchitecturalNote"] =
                    "The production C# LayoutKind.Explicit declaration overlays WorldAreaDetailsPtr (IntPtr) and " +
                    $"CameraStructurePtr (CameraStructure) at +0x{CameraStructureOffset:X}. WorldToScreenMatrix is therefore read at " +
                    $"WorldData + 0x{CameraStructureOffset:X} + 0x{CameraMatrixOffset:X} = +0x{EffectiveCameraMatrixOffset:X}. " +
                    "This probe validates the live structure; it does not infer a new offset.";

                var isMatrixFinite = !float.IsNaN(matrix.M11) && !float.IsInfinity(matrix.M11) &&
                                     !float.IsNaN(matrix.M44) && !float.IsInfinity(matrix.M44);

                if (!isMatrixFinite)
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Fail,
                        $"Matrix elements at +0x{EffectiveCameraMatrixOffset:X} are NaN or Infinity.", details);
                }

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(worldAreaDetailsPtr))
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Warning,
                        $"Matrix is finite, but +0x{WorldAreaDetailsOffset:X} value 0x{worldAreaDetailsPtr.ToInt64():X} is not a canonical WorldAreaDetails pointer in this context.", details);
                }

                if (!areaDetailsValid)
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Warning,
                        "WorldAreaDetails pointer is canonical, but its row pointer did not validate.", details);
                }

                return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Pass,
                    $"Union structurally consistent: AreaDetailsPtr=0x{worldAreaDetailsPtr.ToInt64():X} (+0x{WorldAreaDetailsOffset:X}), row pointer canonical, matrix finite at +0x{EffectiveCameraMatrixOffset:X}", details);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Fail, "Exception during WorldData union audit.", details);
            }
        }

        // =====================================================================
        // Probe 6: Camera / WorldToScreen Projection
        // =====================================================================
        private static V2ProbeResult ProbeCameraWorldToScreen(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var world = Core.States.InGameStateObject.CurrentWorldInstance;
                if (world == null || world.Address == IntPtr.Zero)
                {
                    return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Unavailable,
                        "WorldData object is null or has zero address.", details);
                }

                var worldData = reader.ReadMemory<WorldDataOffset>(world.Address);
                var matrix = worldData.CameraStructurePtr.WorldToScreenMatrix;
                details["EffectiveCameraMatrixOffset"] = $"+0x{EffectiveCameraMatrixOffset:X}";
                details["M11"] = matrix.M11.ToString("F3");
                details["M22"] = matrix.M22.ToString("F3");
                details["M33"] = matrix.M33.ToString("F3");
                details["M44"] = matrix.M44.ToString("F3");

                var isFinite =
                    !float.IsNaN(matrix.M11) && !float.IsInfinity(matrix.M11) &&
                    !float.IsNaN(matrix.M22) && !float.IsInfinity(matrix.M22) &&
                    !float.IsNaN(matrix.M33) && !float.IsInfinity(matrix.M33) &&
                    !float.IsNaN(matrix.M44) && !float.IsInfinity(matrix.M44);

                var isNonZero = matrix != default;

                if (!isFinite || !isNonZero)
                {
                    return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Fail,
                        !isFinite
                            ? "Camera projection matrix contains NaN or Infinite values."
                            : "Camera projection matrix is all zeroes.", details);
                }

                // Live test projection using Player position
                var player = Core.States.InGameStateObject.CurrentAreaInstance?.Player;
                if (player != null && player.TryGetComponent<Render>(out var render))
                {
                    var worldPos = render.WorldPosition;
                    details["PlayerWorldPos"] = $"({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1})";

                    var screen = world.WorldToScreen(worldPos);
                    details["ProjectedScreenPos"] = $"({screen.X:F1}, {screen.Y:F1})";
                    details["WindowArea"] = $"{Core.Process.WindowArea.Width} x {Core.Process.WindowArea.Height}";

                    // Player is typically near the screen center when camera follows
                    var halfW = Core.Process.WindowArea.Width / 2.0f;
                    var halfH = Core.Process.WindowArea.Height / 2.0f;
                    var distFromCenter = Vector2.Distance(screen, new Vector2(halfW, halfH));
                    details["DistanceFromCenter"] = $"{distFromCenter:F1} px";

                    var screenFinite =
                        !float.IsNaN(screen.X) && !float.IsInfinity(screen.X) &&
                        !float.IsNaN(screen.Y) && !float.IsInfinity(screen.Y);
                    if (!screenFinite)
                    {
                        return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Fail,
                            "WorldToScreen returned NaN or Infinity.", details);
                    }

                    var width = Core.Process.WindowArea.Width;
                    var height = Core.Process.WindowArea.Height;
                    if (width <= 0 || height <= 0)
                    {
                        return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Unavailable,
                            "Window dimensions are unavailable, so projected coordinates cannot be range-checked.", details);
                    }

                    var insideClient = screen.X >= 0 && screen.Y >= 0 && screen.X <= width && screen.Y <= height;
                    details["ProjectionInsideClient"] = insideClient.ToString();

                    if (!insideClient)
                    {
                        return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Warning,
                            $"Projection is finite but outside the client area: ({screen.X:F0}, {screen.Y:F0}) vs {width}x{height}.", details);
                    }

                    return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Pass,
                        $"Projection valid: Screen ({screen.X:F0}, {screen.Y:F0}) inside {width}x{height}", details);
                }

                return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Warning,
                    "Camera matrix is non-zero and finite, but Player Render position is unavailable for semantic projection validation.", details);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Fail, "Exception during Camera WorldToScreen probe.", details);
            }
        }

        // =====================================================================
        // Probe 7: ServerData
        // =====================================================================
        private static V2ProbeResult ProbeServerData(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("ServerData", V2ProbeStatus.Unavailable, "InGameState is null.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("ServerData", V2ProbeStatus.Unavailable, "AreaInstanceData is non-canonical.", details);
                }

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + AreaPlayerInfoOffset);
                details["ServerDataPtr"] = $"0x{playerInfo.ServerDataPtr.ToInt64():X}";

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(playerInfo.ServerDataPtr))
                {
                    return new V2ProbeResult("ServerData", V2ProbeStatus.Warning,
                        $"ServerDataPtr 0x{playerInfo.ServerDataPtr.ToInt64():X} is null or non-canonical (not logged in or loading).", details);
                }

                var serverDataOffsets = reader.ReadMemory<ServerDataOffsets>(playerInfo.ServerDataPtr);
                var vecResult = CanonicalStructuralInvariants.ValidateStdVector(serverDataOffsets.PlayerServerDataPtr, elementSize: 8);
                details["PlayerServerDataPtrVector"] = vecResult.Detail;

                if (!vecResult.IsValid)
                {
                    return new V2ProbeResult("ServerData", V2ProbeStatus.Fail,
                        $"PlayerServerDataPtr vector failed invariant: {vecResult.Detail}", details);
                }

                var arr = reader.ReadStdVector<IntPtr>(serverDataOffsets.PlayerServerDataPtr);
                if (arr.Length == 0 || !CanonicalStructuralInvariants.IsCanonicalPointer(arr[0]))
                {
                    return new V2ProbeResult("ServerData", V2ProbeStatus.Warning,
                        "PlayerServerData address in vector is null or non-canonical.", details);
                }

                details["PlayerServerDataAddress"] = $"0x{arr[0].ToInt64():X}";
                return new V2ProbeResult("ServerData", V2ProbeStatus.Pass,
                    $"ServerData block confirmed at 0x{arr[0].ToInt64():X}", details);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("ServerData", V2ProbeStatus.Fail, "Exception during ServerData probe.", details);
            }
        }

        // =====================================================================
        // Probe 8: PlayerInventories
        // =====================================================================
        private static V2ProbeResult ProbePlayerInventories(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (inGameAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable, "InGameState is null.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable, "AreaInstanceData is non-canonical.", details);
                }

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + AreaPlayerInfoOffset);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(playerInfo.ServerDataPtr))
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable, "ServerDataPtr is non-canonical.", details);
                }

                var serverDataOffsets = reader.ReadMemory<ServerDataOffsets>(playerInfo.ServerDataPtr);
                var playerDataVectorValidation =
                    CanonicalStructuralInvariants.ValidateStdVector(serverDataOffsets.PlayerServerDataPtr, elementSize: 8);
                details["PlayerServerDataPtrVector"] = playerDataVectorValidation.Detail;
                if (!playerDataVectorValidation.IsValid)
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Fail,
                        $"PlayerServerDataPtr vector failed invariant: {playerDataVectorValidation.Detail}", details);
                }

                var arr = reader.ReadStdVector<IntPtr>(serverDataOffsets.PlayerServerDataPtr);
                if (arr.Length == 0 || !CanonicalStructuralInvariants.IsCanonicalPointer(arr[0]))
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable,
                        "PlayerServerData array is empty or its first pointer is non-canonical.", details);
                }

                var serverDataStructure = reader.ReadMemory<ServerDataStructure>(arr[0]);
                int elemSize = Marshal.SizeOf<InventoryArrayStruct>();
                var invVec = CanonicalStructuralInvariants.ValidateStdVector(serverDataStructure.PlayerInventories, elementSize: elemSize);
                details["PlayerInventoriesVector"] = invVec.Detail;
                details["InventoryArrayStructSize"] = elemSize.ToString();

                if (!invVec.IsValid)
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Fail,
                        $"PlayerInventories vector validation failed: {invVec.Detail}", details);
                }

                var invArray = reader.ReadStdVector<InventoryArrayStruct>(serverDataStructure.PlayerInventories);
                details["DiscoveredInventoryCount"] = invArray.Length.ToString();

                if (invArray.Length == 0)
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable,
                        "PlayerInventories vector is structurally valid but empty in the current context.", details);
                }

                int validPtrCount = 0;
                for (int i = 0; i < Math.Min(invArray.Length, 15); i++)
                {
                    var id = (InventoryName)invArray[i].InventoryId;
                    var ptr0 = invArray[i].InventoryPtr0;
                    var isCanonical = CanonicalStructuralInvariants.IsCanonicalPointer(ptr0);
                    if (isCanonical) validPtrCount++;
                    details[$"Inv_{i}_{id}"] = $"Id={invArray[i].InventoryId}, Ptr0=0x{ptr0.ToInt64():X}, Valid={isCanonical}";
                }

                if (validPtrCount == 0)
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Warning,
                        $"PlayerInventories contains {invArray.Length} entries, but none of the first {Math.Min(invArray.Length, 15)} pointers are canonical.", details, invArray.Length);
                }

                return new V2ProbeResult("Player Inventories", V2ProbeStatus.Pass,
                    $"Total Inventories: {invArray.Length} (Valid Pointers: {validPtrCount}/{Math.Min(invArray.Length, 15)})", details, invArray.Length);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Player Inventories", V2ProbeStatus.Fail, "Exception during PlayerInventories probe.", details);
            }
        }

        // =====================================================================
        // Probe 9: Inventory / Stash Bounded Item Sampling
        // =====================================================================
        private static V2ProbeResult ProbeInventoryItemSampling(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var liveServerData = Core.States.InGameStateObject.CurrentAreaInstance?.ServerDataObject;
                if (liveServerData == null || liveServerData.Address == IntPtr.Zero)
                {
                    return new V2ProbeResult("Inventory Item Sampling", V2ProbeStatus.Unavailable,
                        "ServerDataObject remote object is not initialized.", details);
                }

                // Check MainInventory1 or Flask1 or any populated inventory
                IntPtr targetInvAddr = IntPtr.Zero;
                string targetInvName = "None";

                if (liveServerData.PlayerInventories.TryGetValue(InventoryName.MainInventory1, out var mainAddr) &&
                    CanonicalStructuralInvariants.IsCanonicalPointer(mainAddr))
                {
                    targetInvAddr = mainAddr;
                    targetInvName = "MainInventory1";
                }
                else if (liveServerData.PlayerInventories.TryGetValue(InventoryName.Flask1, out var flaskAddr) &&
                         CanonicalStructuralInvariants.IsCanonicalPointer(flaskAddr))
                {
                    targetInvAddr = flaskAddr;
                    targetInvName = "Flask1";
                }
                else
                {
                    // Fallback to any canonical inventory pointer
                    foreach (var kvp in liveServerData.PlayerInventories)
                    {
                        if (CanonicalStructuralInvariants.IsCanonicalPointer(kvp.Value))
                        {
                            targetInvAddr = kvp.Value;
                            targetInvName = kvp.Key.ToString();
                            break;
                        }
                    }
                }

                if (targetInvAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Inventory Item Sampling", V2ProbeStatus.Unavailable,
                        "No canonical inventory pointer available in PlayerInventories.", details);
                }

                return ProbeInventoryAddress(reader, "Inventory Item Sampling", targetInvName, targetInvAddr);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Inventory Item Sampling", V2ProbeStatus.Fail, "Exception during ItemSampling probe.", details);
            }
        }

        // =====================================================================
        // Probe 10: Stash Inventory Context
        // =====================================================================
        private static V2ProbeResult ProbeStashInventoryContext(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var inGameAddr = Core.States.InGameStateObject.Address;
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameAddr))
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "InGameState is unavailable in the current context.", details);
                }

                var inGameState = reader.ReadMemory<InGameStateOffset>(inGameAddr);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inGameState.AreaInstanceData))
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "AreaInstanceData is unavailable in the current context.", details);
                }

                var playerInfo =
                    reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + AreaPlayerInfoOffset);
                details["ServerDataPtr"] = $"0x{playerInfo.ServerDataPtr.ToInt64():X}";
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(playerInfo.ServerDataPtr))
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "ServerDataPtr is unavailable in the current context.", details);
                }

                var serverDataOffsets = reader.ReadMemory<ServerDataOffsets>(playerInfo.ServerDataPtr);
                var playerDataVectorValidation =
                    CanonicalStructuralInvariants.ValidateStdVector(serverDataOffsets.PlayerServerDataPtr, elementSize: 8);
                details["PlayerServerDataPtrVector"] = playerDataVectorValidation.Detail;
                if (!playerDataVectorValidation.IsValid)
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Fail,
                        $"PlayerServerDataPtr vector failed invariant: {playerDataVectorValidation.Detail}", details);
                }

                var playerServerData = reader.ReadStdVector<IntPtr>(serverDataOffsets.PlayerServerDataPtr);
                if (playerServerData.Length == 0 ||
                    !CanonicalStructuralInvariants.IsCanonicalPointer(playerServerData[0]))
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "PlayerServerData pointer is unavailable in the current context.", details);
                }

                details["PlayerServerDataAddress"] = $"0x{playerServerData[0].ToInt64():X}";
                var serverDataStructure = reader.ReadMemory<ServerDataStructure>(playerServerData[0]);
                var inventoryElementSize = Marshal.SizeOf<InventoryArrayStruct>();
                var inventoryVectorValidation =
                    CanonicalStructuralInvariants.ValidateStdVector(
                        serverDataStructure.PlayerInventories,
                        elementSize: inventoryElementSize);
                details["PlayerInventoriesVector"] = inventoryVectorValidation.Detail;
                if (!inventoryVectorValidation.IsValid)
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Fail,
                        $"PlayerInventories vector failed invariant: {inventoryVectorValidation.Detail}", details);
                }

                var inventories = reader.ReadStdVector<InventoryArrayStruct>(serverDataStructure.PlayerInventories);
                details["DiscoveredInventoryCount"] = inventories.Length.ToString();
                details["InventoryId"] = ((int)InventoryName.StashInventoryId).ToString();

                bool stashEntryFound = false;
                IntPtr stashAddr = IntPtr.Zero;
                foreach (var inventory in inventories)
                {
                    if (inventory.InventoryId != (int)InventoryName.StashInventoryId)
                    {
                        continue;
                    }

                    stashEntryFound = true;
                    stashAddr = inventory.InventoryPtr0;
                    break;
                }

                if (!stashEntryFound)
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "StashInventoryId entry is not present in the live PlayerInventories vector.", details);
                }

                details["StashInventoryAddress"] = $"0x{stashAddr.ToInt64():X}";
                if (stashAddr == IntPtr.Zero)
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Unavailable,
                        "StashInventoryId is present but its live pointer is null; stash is not currently available.", details);
                }

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(stashAddr))
                {
                    return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Warning,
                        $"Live StashInventoryId pointer 0x{stashAddr.ToInt64():X} is non-zero but non-canonical.", details);
                }

                var result = ProbeInventoryAddress(reader, "Stash Inventory Context", "StashInventoryId", stashAddr);
                foreach (var detail in details)
                {
                    result.Details.TryAdd(detail.Key, detail.Value);
                }

                result.Details["InventorySource"] = "live ServerData -> PlayerServerData -> PlayerInventories";
                return result;
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Stash Inventory Context", V2ProbeStatus.Fail,
                    "Exception during live stash inventory probe.", details);
            }
        }

        private static V2ProbeResult ProbeInventoryAddress(
            SafeMemoryHandle reader,
            string probeName,
            string inventoryName,
            IntPtr inventoryAddress)
        {
            var details = new Dictionary<string, string>
            {
                ["TargetInventory"] = inventoryName,
                ["InventoryAddress"] = $"0x{inventoryAddress.ToInt64():X}",
            };

            try
            {
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(inventoryAddress))
                {
                    return new V2ProbeResult(probeName, V2ProbeStatus.Warning,
                        $"Inventory '{inventoryName}' pointer 0x{inventoryAddress.ToInt64():X} is non-canonical.", details);
                }

                var invStruct = reader.ReadMemory<InventoryStruct>(inventoryAddress);
                details["TotalBoxes"] = $"{invStruct.TotalBoxes.X} x {invStruct.TotalBoxes.Y}";
                details["ServerRequestCounter"] = invStruct.ServerRequestCounter.ToString();

                var itemVecResult = CanonicalStructuralInvariants.ValidateStdVector(invStruct.ItemList, elementSize: 8);
                details["ItemListVector"] = itemVecResult.Detail;
                if (!itemVecResult.IsValid)
                {
                    return new V2ProbeResult(probeName, V2ProbeStatus.Fail,
                        $"Inventory ItemList vector failed structural validation: {itemVecResult.Detail}", details);
                }

                var itemSlots = reader.ReadStdVector<IntPtr>(invStruct.ItemList);
                details["TotalSlotEntries"] = itemSlots.Length.ToString();

                int sampledItems = 0;
                int validatedItems = 0;
                var distinctItemPtrs = new HashSet<IntPtr>();

                foreach (var slotPtr in itemSlots)
                {
                    if (slotPtr == IntPtr.Zero || !CanonicalStructuralInvariants.IsCanonicalPointer(slotPtr))
                    {
                        continue;
                    }

                    if (!distinctItemPtrs.Add(slotPtr))
                    {
                        continue;
                    }

                    var invItem = reader.ReadMemory<InventoryItemStruct>(slotPtr);
                    details[$"Item_{sampledItems}_Slot"] =
                        $"Start=({invItem.SlotStart.X},{invItem.SlotStart.Y}), End=({invItem.SlotEnd.X},{invItem.SlotEnd.Y}), ItemPtr=0x{invItem.Item.ToInt64():X}";

                    if (CanonicalStructuralInvariants.IsCanonicalPointer(invItem.Item))
                    {
                        var itemEnt = reader.ReadMemory<ItemStruct>(invItem.Item);
                        if (CanonicalStructuralInvariants.IsCanonicalPointer(itemEnt.EntityDetailsPtr))
                        {
                            var detailsStruct = reader.ReadMemory<EntityDetails>(itemEnt.EntityDetailsPtr);
                            var path = reader.ReadStdWString(detailsStruct.name);
                            details[$"Item_{sampledItems}_Path"] = path;
                            if (path.StartsWith("Metadata/Items/", StringComparison.OrdinalIgnoreCase))
                            {
                                validatedItems++;
                            }
                        }
                    }

                    sampledItems++;
                    if (sampledItems >= 5)
                    {
                        break;
                    }
                }

                details["SampledItemCount"] = sampledItems.ToString();
                details["ValidatedItemCount"] = validatedItems.ToString();

                if (sampledItems > 0 && validatedItems == 0)
                {
                    return new V2ProbeResult(probeName, V2ProbeStatus.Warning,
                        $"Inventory '{inventoryName}' yielded {sampledItems} sampled wrappers, but none resolved to a canonical Metadata/Items entity.",
                        details,
                        sampledItems);
                }

                return new V2ProbeResult(probeName, V2ProbeStatus.Pass,
                    $"Inventory '{inventoryName}' ({invStruct.TotalBoxes.X}x{invStruct.TotalBoxes.Y}): sampled {sampledItems} distinct items, validated {validatedItems}.",
                    details,
                    sampledItems);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult(probeName, V2ProbeStatus.Fail,
                    $"Exception while validating inventory '{inventoryName}'.", details);
            }
        }

        // =====================================================================
        // Probe 11: Entity / Component Validation
        // =====================================================================
        private static V2ProbeResult ProbeEntityComponents(SafeMemoryHandle reader)
        {
            var details = new Dictionary<string, string>();
            try
            {
                var player = Core.States.InGameStateObject.CurrentAreaInstance?.Player;
                if (player == null || player.Address == IntPtr.Zero)
                {
                    return new V2ProbeResult("Entity Components", V2ProbeStatus.Unavailable,
                        "LocalPlayer RemoteObject is not initialized.", details);
                }

                details["PlayerAddress"] = $"0x{player.Address.ToInt64():X}";
                int componentsSeen = 0;
                int componentsVerified = 0;
                int componentsFailed = 0;

                // 1. Render component
                if (player.TryGetComponent<Render>(out var render) && render.Address != IntPtr.Zero)
                {
                    componentsSeen++;
                    details["Render.Address"] = $"0x{render.Address.ToInt64():X}";
                    details["Render.GridPosition"] = $"({render.GridPosition.X:F1}, {render.GridPosition.Y:F1})";
                    details["Render.TerrainHeight"] = render.TerrainHeight.ToString("F1");

                    var header = reader.ReadMemory<ComponentHeader>(render.Address);
                    var ownerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(header.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Render.OwnerValidation"] = ownerCheck.Detail;
                    if (ownerCheck.IsValid) componentsVerified++;
                    else componentsFailed++;
                }

                // 2. Life component
                if (player.TryGetComponent<Life>(out var life) && life.Address != IntPtr.Zero)
                {
                    componentsSeen++;
                    details["Life.Address"] = $"0x{life.Address.ToInt64():X}";
                    details["Life.HP"] = $"{life.Health.Current} / {life.Health.Total}";
                    details["Life.Mana"] = $"{life.Mana.Current} / {life.Mana.Total}";
                    details["Life.ES"] = $"{life.EnergyShield.Current} / {life.EnergyShield.Total}";

                    var lifeHeader = reader.ReadMemory<ComponentHeader>(life.Address);
                    var lifeOwnerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(lifeHeader.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Life.OwnerValidation"] = lifeOwnerCheck.Detail;

                    bool hpOk =
                        life.Health.Total > 0 &&
                        life.Health.Current >= 0 &&
                        life.Health.Current <= life.Health.Total &&
                        life.Health.Total <= 50000;
                    details["Life.HpRangePlausible"] = hpOk.ToString();

                    if (hpOk && lifeOwnerCheck.IsValid) componentsVerified++;
                    else componentsFailed++;
                }

                // 3. Positioned component
                if (player.TryGetComponent<Positioned>(out var pos) && pos.Address != IntPtr.Zero)
                {
                    componentsSeen++;
                    details["Positioned.Address"] = $"0x{pos.Address.ToInt64():X}";
                    details["Positioned.Flags"] = $"0x{pos.Flags:X2}";
                    details["Positioned.IsFriendly"] = pos.IsFriendly.ToString();
                    var posHeader = reader.ReadMemory<PositionedOffsets>(pos.Address);
                    var posOwnerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(posHeader.Header.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Positioned.OwnerValidation"] = posOwnerCheck.Detail;
                    if (posOwnerCheck.IsValid) componentsVerified++;
                    else componentsFailed++;
                }

                // 4. Actor component
                if (player.TryGetComponent<Actor>(out var actor) && actor.Address != IntPtr.Zero)
                {
                    componentsSeen++;
                    details["Actor.Address"] = $"0x{actor.Address.ToInt64():X}";
                    details["Actor.Animation"] = actor.Animation.ToString();
                    details["Actor.ActiveSkillsCount"] = actor.ActiveSkills.Count.ToString();

                    var actorHeader = reader.ReadMemory<ComponentHeader>(actor.Address);
                    var actorOwnerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(actorHeader.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Actor.OwnerValidation"] = actorOwnerCheck.Detail;
                    if (actorOwnerCheck.IsValid) componentsVerified++;
                    else componentsFailed++;
                }

                details["ComponentsSeen"] = componentsSeen.ToString();
                details["ComponentsVerified"] = componentsVerified.ToString();
                details["ComponentsFailed"] = componentsFailed.ToString();

                if (componentsSeen == 0)
                {
                    return new V2ProbeResult("Entity Components", V2ProbeStatus.Warning,
                        "None of the four practical LocalPlayer components were available in this context.", details);
                }

                if (componentsFailed > 0)
                {
                    return new V2ProbeResult("Entity Components", V2ProbeStatus.Warning,
                        $"Validated {componentsVerified}/{componentsSeen} available components; {componentsFailed} failed owner/range invariants.", details, componentsVerified);
                }

                return new V2ProbeResult("Entity Components", V2ProbeStatus.Pass,
                    $"Validated all {componentsVerified}/{componentsSeen} available practical components on LocalPlayer.", details, componentsVerified);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Entity Components", V2ProbeStatus.Fail, "Exception during Component probe.", details);
            }
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 0x20)]
        private struct MapNodeHeader
        {
            [FieldOffset(0x00)] public IntPtr Left;
            [FieldOffset(0x08)] public IntPtr Parent;
            [FieldOffset(0x10)] public IntPtr Right;
            [FieldOffset(0x18)] public byte Color;
            [FieldOffset(0x19)] public byte IsNil;
        }

        private static void ObserveSessionEvidence(SafeMemoryHandle reader)
        {
            byte? isLoading = null;
            long areaInstance = 0;
            uint areaHash = 0;
            long localPlayer = 0;
            long worldData = 0;

            try
            {
                var loadingAddr = Core.States.AreaLoading.Address;
                if (CanonicalStructuralInvariants.IsCanonicalPointer(loadingAddr) &&
                    reader.TryReadMemory<AreaLoadingStateOffset>(loadingAddr, out var loadingData) &&
                    loadingData.IsLoading is 0 or 1)
                {
                    isLoading = (byte)loadingData.IsLoading;
                }

                var inGameAddr = Core.States.InGameStateObject.Address;
                if (CanonicalStructuralInvariants.IsCanonicalPointer(inGameAddr) &&
                    reader.TryReadMemory<InGameStateOffset>(inGameAddr, out var inGameData))
                {
                    if (CanonicalStructuralInvariants.IsCanonicalPointer(inGameData.AreaInstanceData))
                    {
                        areaInstance = inGameData.AreaInstanceData.ToInt64();

                        if (reader.TryReadMemory<AreaInstanceOffsets>(inGameData.AreaInstanceData, out var areaData))
                        {
                            areaHash = areaData.CurrentAreaHash;
                            if (CanonicalStructuralInvariants.IsCanonicalPointer(areaData.PlayerInfo.LocalPlayerPtr))
                            {
                                localPlayer = areaData.PlayerInfo.LocalPlayerPtr.ToInt64();
                            }
                        }
                    }

                    if (CanonicalStructuralInvariants.IsCanonicalPointer(inGameData.WorldData))
                    {
                        worldData = inGameData.WorldData.ToInt64();
                    }
                }
            }
            catch
            {
                // Session evidence is supplementary. Individual probes own detailed failures.
            }

            RecordSessionEvidenceSample(
                isLoading,
                areaInstance,
                areaHash,
                localPlayer,
                worldData,
                DateTime.UtcNow);
        }

        private static V2PracticalReport BuildReport(
            string gameState,
            string procInfo,
            List<V2ProbeResult> probes,
            long startedTimestamp)
        {
            int pass = 0, warn = 0, fail = 0, unavail = 0;
            foreach (var p in probes)
            {
                switch (p.Status)
                {
                    case V2ProbeStatus.Pass: pass++; break;
                    case V2ProbeStatus.Warning: warn++; break;
                    case V2ProbeStatus.Fail: fail++; break;
                    case V2ProbeStatus.Unavailable: unavail++; break;
                }
            }

            return new V2PracticalReport(
                DateTime.UtcNow,
                gameState,
                procInfo,
                probes,
                pass,
                warn,
                fail,
                unavail,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                GetSessionEvidence());
        }
    }
}

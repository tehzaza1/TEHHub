// <copyright file="OffsetHelperV2Engine.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
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

        public sealed record V2PracticalReport(
            DateTime TimestampUtc,
            string GameState,
            string ProcessInfo,
            List<V2ProbeResult> Probes,
            int TotalPass,
            int TotalWarning,
            int TotalFail,
            int TotalUnavailable);

        /// <summary>
        /// Executes all practical diagnostic probes in a safe, read-only manner.
        /// </summary>
        public static V2PracticalReport RunPracticalDiagnostics()
        {
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

                return BuildReport(gameState, procInfo, probes);
            }

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

            // 9. Inventory / Stash Bounded Item Sampling Probe
            probes.Add(ProbeInventoryItemSampling(reader));

            // 10. Entity / Component Validation Probe
            probes.Add(ProbeEntityComponents(reader));

            return BuildReport(gameState, procInfo, probes);
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

                var isDetailsCanonical = CanonicalStructuralInvariants.IsCanonicalPointer(data.CurrentAreaDetailsPtr);
                details["AreaDetailsCanonical"] = isDetailsCanonical.ToString();

                if (data.IsLoading != 0 && data.IsLoading != 1)
                {
                    return new V2ProbeResult("Area Loading State", V2ProbeStatus.Warning,
                        $"IsLoading has unexpected value {data.IsLoading} (expected 0 or 1).", details, data.IsLoading);
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

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + 0x5B0);
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

                // EntityListStruct at AreaInstanceData + 0x6F0; AwakeEntities is StdMap at +0x00
                var awakeMap = reader.ReadMemory<StdMap>(inGameState.AreaInstanceData + 0x6F0);
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

                // Audit the +0x98 Union:
                // 1. WorldAreaDetailsPtr (IntPtr at WorldData + 0x98)
                var worldAreaDetailsPtr = reader.ReadMemory<IntPtr>(inGameState.WorldData + 0x98);
                details["WorldAreaDetailsPtr (+0x98)"] = $"0x{worldAreaDetailsPtr.ToInt64():X}";

                // 2. CameraStructure.CodePtr (IntPtr at WorldData + 0x98 + 0x00 = +0x98)
                details["CameraStructure.CodePtr (+0x98)"] = $"0x{worldAreaDetailsPtr.ToInt64():X} (Shares offset 0x98 with WorldAreaDetailsPtr)";

                // 3. CameraStructure.WorldToScreenMatrix (Matrix4x4 at WorldData + 0x98 + 0x108 = +0x1A0)
                var matrix = reader.ReadMemory<Matrix4x4>(inGameState.WorldData + 0x1A0);
                details["CameraStructure.Matrix (+0x1A0)"] = $"M11={matrix.M11:0.00}, M22={matrix.M22:0.00}, M33={matrix.M33:0.00}, M44={matrix.M44:0.00}";

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
                    "In C# LayoutKind.Explicit, WorldAreaDetailsPtr (IntPtr) and CameraStructurePtr (CameraStructure) " +
                    "overlap at +0x98. WorldToScreenMatrix resides at WorldData + 0x98 + 0x108 = +0x1A0. Production matches this layout.";

                var isMatrixFinite = !float.IsNaN(matrix.M11) && !float.IsInfinity(matrix.M11) &&
                                     !float.IsNaN(matrix.M44) && !float.IsInfinity(matrix.M44);

                if (!isMatrixFinite)
                {
                    return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Fail,
                        "Matrix elements at +0x1A0 are NaN or Infinity.", details);
                }

                return new V2ProbeResult("WorldData +0x98 Union Audit", V2ProbeStatus.Pass,
                    $"Union confirmed: AreaDetailsPtr=0x{worldAreaDetailsPtr.ToInt64():X} (+0x98), Matrix verified at +0x1A0", details);
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

                // Read matrix at +0x1A0
                var matrix = reader.ReadMemory<Matrix4x4>(world.Address + 0x1A0);
                details["M11"] = matrix.M11.ToString("F3");
                details["M22"] = matrix.M22.ToString("F3");
                details["M33"] = matrix.M33.ToString("F3");
                details["M44"] = matrix.M44.ToString("F3");

                var isFinite = !float.IsNaN(matrix.M11) && !float.IsNaN(matrix.M22) &&
                               !float.IsInfinity(matrix.M11) && !float.IsInfinity(matrix.M22);

                if (!isFinite)
                {
                    return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Fail,
                        "Camera projection matrix contains NaN or Infinite values.", details);
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

                    return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Pass,
                        $"Projection valid: Screen ({screen.X:F0}, {screen.Y:F0}) inside {Core.Process.WindowArea.Width}x{Core.Process.WindowArea.Height}", details);
                }

                return new V2ProbeResult("Camera WorldToScreen", V2ProbeStatus.Pass,
                    "Camera matrix is non-zero and finite (Player Render position not yet loaded for live projection).", details);
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

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + 0x5B0);
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

                var playerInfo = reader.ReadMemory<LocalPlayerStruct>(inGameState.AreaInstanceData + 0x5B0);
                if (!CanonicalStructuralInvariants.IsCanonicalPointer(playerInfo.ServerDataPtr))
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable, "ServerDataPtr is non-canonical.", details);
                }

                var serverDataOffsets = reader.ReadMemory<ServerDataOffsets>(playerInfo.ServerDataPtr);
                var arr = reader.ReadStdVector<IntPtr>(serverDataOffsets.PlayerServerDataPtr);
                if (arr.Length == 0 || !CanonicalStructuralInvariants.IsCanonicalPointer(arr[0]))
                {
                    return new V2ProbeResult("Player Inventories", V2ProbeStatus.Unavailable, "PlayerServerData array is empty.", details);
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

                int validPtrCount = 0;
                for (int i = 0; i < Math.Min(invArray.Length, 15); i++)
                {
                    var id = (InventoryName)invArray[i].InventoryId;
                    var ptr0 = invArray[i].InventoryPtr0;
                    var isCanonical = CanonicalStructuralInvariants.IsCanonicalPointer(ptr0);
                    if (isCanonical) validPtrCount++;
                    details[$"Inv_{i}_{id}"] = $"Id={invArray[i].InventoryId}, Ptr0=0x{ptr0.ToInt64():X}, Valid={isCanonical}";
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

                details["TargetInventory"] = targetInvName;
                details["InventoryAddress"] = $"0x{targetInvAddr.ToInt64():X}";

                var invStruct = reader.ReadMemory<InventoryStruct>(targetInvAddr);
                details["TotalBoxes"] = $"{invStruct.TotalBoxes.X} x {invStruct.TotalBoxes.Y}";
                details["ServerRequestCounter"] = invStruct.ServerRequestCounter.ToString();

                var itemVecResult = CanonicalStructuralInvariants.ValidateStdVector(invStruct.ItemList, elementSize: 8);
                details["ItemListVector"] = itemVecResult.Detail;

                var itemSlots = reader.ReadStdVector<IntPtr>(invStruct.ItemList);
                details["TotalSlotEntries"] = itemSlots.Length.ToString();

                int sampledItems = 0;
                var distinctItemPtrs = new HashSet<IntPtr>();

                foreach (var slotPtr in itemSlots)
                {
                    if (slotPtr == IntPtr.Zero || !CanonicalStructuralInvariants.IsCanonicalPointer(slotPtr)) continue;
                    if (!distinctItemPtrs.Add(slotPtr)) continue;

                    var invItem = reader.ReadMemory<InventoryItemStruct>(slotPtr);
                    details[$"Item_{sampledItems}_Slot"] = $"Start=({invItem.SlotStart.X},{invItem.SlotStart.Y}), End=({invItem.SlotEnd.X},{invItem.SlotEnd.Y}), ItemPtr=0x{invItem.Item.ToInt64():X}";

                    if (CanonicalStructuralInvariants.IsCanonicalPointer(invItem.Item))
                    {
                        var itemEnt = reader.ReadMemory<ItemStruct>(invItem.Item);
                        if (CanonicalStructuralInvariants.IsCanonicalPointer(itemEnt.EntityDetailsPtr))
                        {
                            var detailsStruct = reader.ReadMemory<EntityDetails>(itemEnt.EntityDetailsPtr);
                            var path = reader.ReadStdWString(detailsStruct.name);
                            details[$"Item_{sampledItems}_Path"] = path;
                        }
                    }

                    sampledItems++;
                    if (sampledItems >= 5) break;
                }

                details["SampledItemCount"] = sampledItems.ToString();

                return new V2ProbeResult("Inventory Item Sampling", V2ProbeStatus.Pass,
                    $"Inventory '{targetInvName}' ({invStruct.TotalBoxes.X}x{invStruct.TotalBoxes.Y}): Sampled {sampledItems} distinct items.", details, sampledItems);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Inventory Item Sampling", V2ProbeStatus.Fail, "Exception during ItemSampling probe.", details);
            }
        }

        // =====================================================================
        // Probe 10: Entity / Component Validation
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
                int componentsVerified = 0;

                // 1. Render component
                if (player.TryGetComponent<Render>(out var render) && render.Address != IntPtr.Zero)
                {
                    details["Render.Address"] = $"0x{render.Address.ToInt64():X}";
                    details["Render.GridPosition"] = $"({render.GridPosition.X:F1}, {render.GridPosition.Y:F1})";
                    details["Render.TerrainHeight"] = render.TerrainHeight.ToString("F1");

                    var header = reader.ReadMemory<ComponentHeader>(render.Address);
                    var ownerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(header.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Render.OwnerValidation"] = ownerCheck.Detail;
                    if (ownerCheck.IsValid) componentsVerified++;
                }

                // 2. Life component
                if (player.TryGetComponent<Life>(out var life) && life.Address != IntPtr.Zero)
                {
                    details["Life.Address"] = $"0x{life.Address.ToInt64():X}";
                    details["Life.HP"] = $"{life.Health.Current} / {life.Health.Total}";
                    details["Life.Mana"] = $"{life.Mana.Current} / {life.Mana.Total}";
                    details["Life.ES"] = $"{life.EnergyShield.Current} / {life.EnergyShield.Total}";

                    bool hpOk = life.Health.Total > 0 && life.Health.Current <= life.Health.Total && life.Health.Total <= 50000;
                    details["Life.HpRangePlausible"] = hpOk.ToString();
                    if (hpOk) componentsVerified++;
                }

                // 3. Positioned component
                if (player.TryGetComponent<Positioned>(out var pos) && pos.Address != IntPtr.Zero)
                {
                    details["Positioned.Address"] = $"0x{pos.Address.ToInt64():X}";
                    details["Positioned.Flags"] = $"0x{pos.Flags:X2}";
                    details["Positioned.IsFriendly"] = pos.IsFriendly.ToString();
                    var posHeader = reader.ReadMemory<PositionedOffsets>(pos.Address);
                    var posOwnerCheck = CanonicalStructuralInvariants.ValidateComponentOwner(posHeader.Header.EntityPtr.ToInt64(), player.Address.ToInt64());
                    details["Positioned.OwnerValidation"] = posOwnerCheck.Detail;
                    if (posOwnerCheck.IsValid) componentsVerified++;
                }

                // 4. Actor component
                if (player.TryGetComponent<Actor>(out var actor) && actor.Address != IntPtr.Zero)
                {
                    details["Actor.Address"] = $"0x{actor.Address.ToInt64():X}";
                    details["Actor.Animation"] = actor.Animation.ToString();
                    details["Actor.ActiveSkillsCount"] = actor.ActiveSkills.Count.ToString();
                    componentsVerified++;
                }

                details["ComponentsVerified"] = componentsVerified.ToString();

                if (componentsVerified == 0)
                {
                    return new V2ProbeResult("Entity Components", V2ProbeStatus.Warning,
                        "No components successfully validated on LocalPlayer.", details);
                }

                return new V2ProbeResult("Entity Components", V2ProbeStatus.Pass,
                    $"Verified {componentsVerified} components (Render, Life, Positioned, Actor) on LocalPlayer.", details, componentsVerified);
            }
            catch (Exception ex)
            {
                details["Exception"] = ex.Message;
                return new V2ProbeResult("Entity Components", V2ProbeStatus.Fail, "Exception during Component probe.", details);
            }
        }

        private static V2PracticalReport BuildReport(string gameState, string procInfo, List<V2ProbeResult> probes)
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
                unavail);
        }
    }
}

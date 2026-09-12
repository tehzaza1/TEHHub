// <copyright file="KrangledPassiveDetector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using Coroutine;
    using TEHhub.CoroutineEvents;
    using ImGuiNET;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    ///     Detect Krangled Passive in the POE event and
    ///     export it to a file readable by POB.
    /// </summary>
    public static class KrangledPassiveDetector
    {
        /// <summary>
        ///     Initializes the co-routines.
        /// </summary>
        internal static void InitializeCoroutines()
        {
#if DEBUG
            CoroutineHandler.Start(KrangledPassiveDetectorCoRoutine(), priority: UiRenderPriority.CoreWindows);
#endif
        }
#if DEBUG
        /// <summary>
        ///     Draws the window for detecting and getting Krangled Passive.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> KrangledPassiveDetectorCoRoutine()
        {
            Vector2 size = new(624, 380);
            Dictionary<Vector2, int> standardSkillTree = new();
            Dictionary<Vector2, int> krangledSkillTree = new();
            List<int> skillMissingInStandard = new();
            List<int> skillMissingInKrangled = new();
            Dictionary<int, int> skillConvertor = new();
            var messageToDisplay = string.Empty;
            var dataJsonFilePath = string.Empty;
            while (true)
            {
                yield return new Wait(TEHhubEvents.OnRender);
                if (!Core.GHSettings.ShowKrangledPassiveDetector)
                {
                    continue;
                }

                var skillTreeNodes = Core.States.InGameStateObject.GameUi.SkillTreeNodesUiElements;
                ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
                if (ImGui.Begin("Krangled Passive Detector", ref Core.GHSettings.ShowKrangledPassiveDetector))
                {
                    if (ImGui.BeginPopup("KrangledPassiveDetectorPopUp"))
                    {
                        ImGui.Text(messageToDisplay);
                        ImGui.Separator();
                        if (ImGui.Button("Ok"))
                        {
                            ImGui.CloseCurrentPopup();
                        }

                        ImGui.EndPopup();
                    }

                    ImGui.TextWrapped("Go to the standard league and open your passive tree and press the following button." +
                        "Please be extra careful and do not move the tree around otherwise this tool will not work.");
                    if (ImGui.Button("Record standard tree data"))
                    {
                        standardSkillTree.Clear();
                        var message = "Standard skills loaded";
                        foreach (var skillTreeNode in skillTreeNodes)
                        {
                            if (standardSkillTree.ContainsKey(skillTreeNode.Position))
                            {
                                message = "ERROR: Duplicates found.";
                            }

                            standardSkillTree[skillTreeNode.Position] = skillTreeNode.SkillGraphId;
                        }

                        messageToDisplay = skillTreeNodes.Count > 0 ? message : "ERROR: Standard skills not found.";
                        ImGui.OpenPopup("KrangledPassiveDetectorPopUp");
                    }

                    ImGui.TextWrapped("Go to the Krangled event league and open your passive tree and press the following button." +
                        "Please be extra careful and do not move the tree around otherwise this tool will not work.");
                    if (ImGui.Button("Record Krangled tree data"))
                    {
                        krangledSkillTree.Clear();
                        var message = "Krangled skills loaded.";
                        foreach (var skillTreeNode in skillTreeNodes)
                        {
                            if (krangledSkillTree.ContainsKey(skillTreeNode.Position))
                            {
                                message = "ERROR: Duplicates found.";
                            }

                            krangledSkillTree[skillTreeNode.Position] = skillTreeNode.SkillGraphId;
                        }

                        messageToDisplay = skillTreeNodes.Count > 0 ? message : "ERROR: Krangled skills not found.";
                        ImGui.OpenPopup("KrangledPassiveDetectorPopUp");
                    }

                    ImGui.TextWrapped("Click the following button to start the conversion process.");
                    ImGui.BeginDisabled(standardSkillTree.Count <= 0 || krangledSkillTree.Count <= 0);
                    if (ImGui.Button("Process Tree data"))
                    {
                        skillMissingInKrangled.Clear();
                        skillMissingInStandard.Clear();
                        skillConvertor.Clear();
                        foreach (var (posStandard, skillId) in standardSkillTree)
                        {
                            if (!krangledSkillTree.ContainsKey(posStandard))
                            {
                                skillMissingInKrangled.Add(skillId);
                            }
                        }

                        foreach (var (posKrangled, skillId) in krangledSkillTree)
                        {
                            if (!standardSkillTree.ContainsKey(posKrangled))
                            {
                                skillMissingInStandard.Add(skillId);
                            }
                        }

                        foreach(var (pos, skillId) in standardSkillTree)
                        {
                            // F-174: use TryGetValue to avoid KeyNotFoundException when pos is
                            // missing from krangledSkillTree (which is exactly the case the
                            // earlier missingInKrangled enumeration tracked - line 117-123).
                            if (!krangledSkillTree.TryGetValue(pos, out var krangledSkillId))
                            {
                                continue;
                            }

                            if (skillConvertor.TryGetValue(skillId, out var value) && value != krangledSkillId)
                            {
                                Console.WriteLine($"Error: {skillId}->{value} (new value {krangledSkillId}) already exists in skill convertor.");
                            }

                            skillConvertor[skillId] = krangledSkillId;
                        }

                        messageToDisplay = "Conversion map generated, read console logs for any errors.";
                        ImGui.OpenPopup("KrangledPassiveDetectorPopUp");
                    }

                    ImGui.EndDisabled();

                    ImGui.TextWrapped("Validate that both trees have no missing skills otherwise something is wrong" +
                        " and report it to this tool dev.");
                    if (ImGui.TreeNode("Skill missing in standard"))
                    {
                        foreach (var skillId in skillMissingInStandard)
                        {
                            ImGui.Text(skillId.ToString());
                        }

                        ImGui.TreePop();
                    }

                    if (ImGui.TreeNode("Skill missing in krangled"))
                    {
                        foreach (var skillId in skillMissingInKrangled)
                        {
                            ImGui.Text(skillId.ToString());
                        }

                        ImGui.TreePop();
                    }

                    if (ImGui.TreeNode("Skill convertor map"))
                    {
                        foreach(var (standardSkillId, krangledSkillId) in skillConvertor)
                        {
                            ImGui.Text($"{standardSkillId} -> {krangledSkillId}");
                        }

                        ImGui.TreePop();
                    }

                    ImGui.BeginDisabled(skillMissingInStandard.Count > 0 || skillMissingInKrangled.Count > 0 || skillConvertor.Count <= 0);
                    ImGui.InputText("Data.json file path", ref dataJsonFilePath, 300);
                    if (ImGui.Button("Generate krangled data.json"))
                    {
                        // This intentionally trusts the Path of Building file shape: a "nodes" object
                        // containing per-skill objects with the copied fields below.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                        var dataReader = JsonNode.Parse(File.ReadAllText(dataJsonFilePath)).AsObject();
                        var dataWriter = dataReader.DeepClone().AsObject();
                        var sourceNodes = dataReader["nodes"].AsObject();
                        var targetNodes = dataWriter["nodes"].AsObject();
                        foreach (var skillStruct in sourceNodes)
                        {
                            if (skillStruct.Key == "root")
                            {
                                continue;
                            }

                            var skillId = int.Parse(skillStruct.Key);
                            if (!skillConvertor.ContainsKey(skillId))
                            {
                                continue;
                            }

                            var krangledSkillId = skillConvertor[skillId];
                            var sourceNode = skillStruct.Value.AsObject();
                            targetNodes[skillStruct.Key] = sourceNodes[krangledSkillId.ToString()].DeepClone();
                            var targetNode = targetNodes[skillStruct.Key].AsObject();
                            targetNode["skill"] = sourceNode["skill"].DeepClone();
                            targetNode["group"] = sourceNode["group"].DeepClone();
                            targetNode["orbit"] = sourceNode["orbit"].DeepClone();
                            targetNode["orbitIndex"] = sourceNode["orbitIndex"].DeepClone();
                            targetNode["out"] = sourceNode["out"].DeepClone();
                            targetNode["in"] = sourceNode["in"].DeepClone();
                        }

                        File.WriteAllText(
                            dataJsonFilePath.Replace(".json", "_krangled.json"),
                            dataWriter.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
#pragma warning restore CS8602
                        messageToDisplay = $"{dataJsonFilePath.Replace(".json", "_krangled.json")} generated.";
                        ImGui.OpenPopup("KrangledPassiveDetectorPopUp");
                    }

                    ImGui.EndDisabled();

                    ImGui.NewLine();

                    ImGui.TextWrapped($"1: Go to POB tree data (normally its in C:\\Users\\<username>\\" +
                        $"AppData\\Roaming\\Path of Building Community\\TreeData\\<league_number>)");
                    ImGui.TextWrapped("2: Rename \"tree.lua\" to \"tree.lua_backup\"");
                    ImGui.TextWrapped("3: Copy the generated \"data_krangled.json\" as \"data.json\"");
                    ImGui.TextWrapped("4: Run path of building software and then close it after it's fully loaded.");
                    ImGui.TextWrapped("5: Copy paste sprites from step-2 lua file backup into the newly generated lua file.");
                }

                ImGui.End();
            }
        }
#endif
    }
}

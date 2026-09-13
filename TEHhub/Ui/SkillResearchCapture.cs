namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TEHhub.RemoteObjects.Components;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.UiElement;

// On-demand research only; all game reads run on the render thread.
internal static class SkillResearchCapture
{
    private static TaskCompletionSource<SkillResearchSnapshot>? pending;

    internal static Task<SkillResearchSnapshot>? Request()
    {
        var request = new TaskCompletionSource<SkillResearchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref pending, request, null) == null ? request.Task : null;
    }

    internal static void Collect()
    {
        var request = Interlocked.Exchange(ref pending, null);
        if (request == null) return;
        try
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var player = area.Player;
            var values = new Dictionary<string, Dictionary<string, string>>();
            if (player.TryGetComponent<Buffs>(out var buffs))
            {
                foreach (var entry in buffs.StatusEffects)
                    values["buff/" + entry.Key] = Fields(entry.Value);
                CaptureBuffBytes(buffs.Address, values);
            }
            if (player.TryGetComponent<Actor>(out var actor))
            {
                foreach (var entry in actor.ActiveSkills)
                    values["skill/" + entry.Key] = Fields(entry.Value);
                foreach (var entry in actor.ActiveSkillCooldowns)
                    values["cooldown/" + entry.Key] = Fields(entry.Value);
            }
            CaptureUi(Core.States.InGameStateObject.GameUi.Address, values);
            request.SetResult(new(DateTime.UtcNow, Environment.ProcessId, area.AreaHash, player.Address.ToString(), values));
        }
        catch (Exception error) { request.SetException(error); }
    }

    private static void CaptureBuffBytes(IntPtr address, Dictionary<string, Dictionary<string, string>> values)
    {
        var reader = Core.Process.Handle;
        if (!reader.TryReadMemory<BuffsOffsets>(address, out var header)) return;
        var count = header.StatusEffectPtr.TotalElements(IntPtr.Size);
        if (count <= 0 || count > 256) return;
        var pointers = new IntPtr[(int)count];
        if (!reader.TryReadMemoryArray(header.StatusEffectPtr.First, pointers, out _)) return;
        foreach (var pointer in pointers)
        {
            var bytes = new byte[0x80];
            if (pointer != IntPtr.Zero && reader.TryReadMemoryArray(pointer, bytes, out _))
                values["raw-buff/" + pointer.ToString("X")] = new() { ["hex"] = Convert.ToHexString(bytes) };
        }
    }

    private static void CaptureUi(IntPtr root, Dictionary<string, Dictionary<string, string>> values)
    {
        var reader = Core.Process.Handle;
        var queue = new Queue<(IntPtr Address, string Path, int Depth)>();
        var visited = new HashSet<IntPtr>();
        var linked = new HashSet<IntPtr>();
        var timer = Stopwatch.StartNew();
        queue.Enqueue((root, "root", 0));
        while (queue.Count > 0 && visited.Count < 16384 && timer.ElapsedMilliseconds < 250)
        {
            var node = queue.Dequeue();
            if (node.Address == IntPtr.Zero || !visited.Add(node.Address)) continue;
            if (!reader.TryReadMemory<UiElementBaseOffset>(node.Address, out var data) || data.Self != node.Address) continue;
            var fields = Fields(data);
            fields["address"] = node.Address.ToString("X");
            if (data.StringIdPtr.Length > 0 && data.StringIdPtr.Length <= 128 && data.StringIdPtr.Capacity >= data.StringIdPtr.Length && data.StringIdPtr.Capacity <= 4096)
                fields["id"] = reader.ReadStdWString(data.StringIdPtr);
            var bytes = new byte[0x400];
            if (reader.TryReadMemoryArray(node.Address, bytes, out _)) fields["hex"] = Convert.ToHexString(bytes);
            values["ui/" + node.Path] = fields;
            // Capture one level of payload pointers on visible compact controls, where buff
            // icons and their count labels can live. Keep failed pointers and truncation explicit.
            if ((data.Flags & 0x800) != 0 && data.ChildrensPtr.TotalElements(IntPtr.Size) <= 16)
            {
                for (var offset = 0x290; offset <= bytes.Length - 8 && linked.Count < 2048 && timer.ElapsedMilliseconds < 250; offset += 8)
                {
                    var candidate = BitConverter.ToInt64(bytes, offset);
                    if (candidate < 0x10000 || candidate > 0x00007FFFFFFFFFFF || (candidate & 7) != 0) continue;
                    var pointer = new IntPtr(candidate);
                    if (!linked.Add(pointer)) continue;
                    var payload = new byte[0x100];
                    var success = reader.TryReadMemoryArray(pointer, payload, out _);
                    values["ui-link/" + node.Path + "/" + offset.ToString("X")] = new()
                    {
                        ["address"] = pointer.ToString("X"), ["readable"] = success.ToString(),
                        ["hex"] = success ? Convert.ToHexString(payload) : "",
                    };
                }
            }
            var count = data.ChildrensPtr.TotalElements(IntPtr.Size);
            if (node.Depth >= 20 || count <= 0 || count > 2048 || queue.Count + count > 32768) continue;
            var children = new IntPtr[(int)count];
            if (!reader.TryReadMemoryArray(data.ChildrensPtr.First, children, out _)) continue;
            for (var i = 0; i < children.Length; i++) queue.Enqueue((children[i], node.Path + "." + i, node.Depth + 1));
        }
        values["research/ui-scan"] = new() { ["visited"] = visited.Count.ToString(),
            ["milliseconds"] = timer.ElapsedMilliseconds.ToString(), ["truncated"] = (queue.Count > 0).ToString(),
            ["linkedPointers"] = linked.Count.ToString(), ["linkedPointerLimitReached"] = (linked.Count >= 2048).ToString() };
    }

    private static Dictionary<string, string> Fields<T>(T value) where T : struct
    {
        var result = new Dictionary<string, string>();
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            result[field.Name] = Convert.ToString(field.GetValue(value), CultureInfo.InvariantCulture) ?? "";
        return result;
    }
}

internal sealed record SkillResearchSnapshot(DateTime Utc, int ProcessId, string AreaHash,
    string PlayerAddress, Dictionary<string, Dictionary<string, string>> Values);

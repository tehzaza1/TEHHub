namespace UiDump;

using System.Text;
using TEHhub;
using TEHhub.Plugin;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.UiElement;

internal static class UiMemoryReader
{
    internal static UiNode? Read(long address, bool includeRaw)
    {
        var reader = Core.Process.Handle;
        var pointer = new IntPtr(address);
        if (!reader.TryReadMemory<UiElementBaseOffset>(pointer, out var data) ||
            (data.Self != IntPtr.Zero && data.Self != pointer)) return null;
        var node = new UiNode
        {
            ParentAddress = $"0x{data.ParentPtr.ToInt64():X}",
            Vtable = $"0x{data.Vtable.ToInt64():X}",
            Flags = data.Flags,
            LocalVisible = UiElementBaseFuncs.IsVisibleChecker(data.Flags),
            LocalPosition = [Finite(data.RelativePosition.X), Finite(data.RelativePosition.Y)],
            LocalSize = [Finite(data.UnscaledSize.X), Finite(data.UnscaledSize.Y)],
        };
        if (node.LocalVisible && PluginUiElementReflection.TryGetAbsoluteRect(pointer, out var pos, out var size))
            node.ScreenRect = [Finite(pos.X), Finite(pos.Y), Finite(size.X), Finite(size.Y)];

        // Known PoE2 candidates already used by AreaModUiReader. These are evidence,
        // not a promise that every UI class stores its displayed text at these offsets.
        foreach (int offset in new[] { 0x128, 0x2E0, 0x360 })
        {
            var text = ReadText(pointer + offset);
            if (!string.IsNullOrEmpty(text)) node.TextCandidates[$"0x{offset:X}"] = text;
        }
        if (includeRaw)
        {
            var bytes = new byte[0x500];
            if (reader.TryReadMemoryArray(pointer, bytes, out _)) node.RawHex = Convert.ToHexString(bytes);
            else node.Errors.Add("Optional 0x500-byte UI payload unreadable");
        }
        // Existing NinjaPricer slot candidate. Only retain independently validated items.
        if (reader.TryReadMemory<IntPtr>(pointer + 0x4E0, out var item) && item != IntPtr.Zero &&
            PluginUiElementReflection.TryValidateItemAddress(item, out var path, out _))
        {
            node.ItemAddress = $"0x{item.ToInt64():X}";
            node.ItemPath = path;
        }

        long first = data.ChildrensPtr.First.ToInt64();
        long last = data.ChildrensPtr.Last.ToInt64();
        long end = data.ChildrensPtr.End.ToInt64();
        if (first < 0 || last < first || end < last || (last - first) % IntPtr.Size != 0 ||
            last - first > 2048 * IntPtr.Size || (first == 0 && last != 0))
        {
            node.Errors.Add("Invalid or oversized child vector (limit 2048)");
            return node;
        }
        int count = (int)((last - first) / IntPtr.Size);
        node.DeclaredChildren = count;
        if (count > 0)
        {
            var children = new long[count];
            if (reader.TryReadMemoryArray(data.ChildrensPtr.First, children, out _)) node.Children = children;
            else node.Errors.Add("Child vector unreadable");
        }
        return node;
    }

    private static float? Finite(float value) => float.IsFinite(value) ? value : null;

    private static string? ReadText(IntPtr address)
    {
        var reader = Core.Process.Handle;
        if (!reader.TryReadMemory<StdWString>(address, out var text) ||
            text.PAD_14 != 0 || text.PAD_1C != 0 || text.Length <= 0 || text.Length > 1024 ||
            text.Capacity < text.Length || text.Capacity > 65536) return null;
        var buffer = new byte[text.Length * 2];
        var source = text.Capacity <= 8 ? address : text.Buffer;
        if (!reader.TryReadMemoryArray(source, buffer, out _)) return null;
        var value = Encoding.Unicode.GetString(buffer);
        if (value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) return null;
        return value;
    }
}

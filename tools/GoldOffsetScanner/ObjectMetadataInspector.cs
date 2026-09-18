namespace GoldOffsetScanner
{
    using System;
    using System.Text;

    public static class ObjectMetadataInspector
    {
        public static void InspectObject(NativeMemoryReader reader, ulong objAddr)
        {
            Console.WriteLine($"\n[Metadata Inspection for Object 0x{objAddr:X12}]");
            for (var offset = 0; offset < 0x100; offset += 8)
            {
                var fieldAddr = (IntPtr)(long)(objAddr + (ulong)offset);
                if (reader.TryRead<ulong>(fieldAddr, out var val64))
                {
                    var extra = "";
                    if (val64 >= 0x10000 && val64 <= 0x7FFFFFFFFFFF)
                    {
                        // Check if string
                        var strBuf = new byte[64];
                        if (reader.TryReadBytes((IntPtr)(long)val64, strBuf, out var strRead) && strRead > 0)
                        {
                            var ascii = Encoding.ASCII.GetString(strBuf).Split('\0')[0];
                            var unicode = Encoding.Unicode.GetString(strBuf).Split('\0')[0];
                            if (ascii.Length >= 3 && ascii.All(c => c >= 32 && c <= 126))
                            {
                                extra = $"-> ASCII \"{ascii}\"";
                            }
                            else if (unicode.Length >= 3 && unicode.All(c => c >= 32 && c <= 126))
                            {
                                extra = $"-> UTF-16 \"{unicode}\"";
                            }
                        }
                    }
                    reader.TryRead<int>(fieldAddr, out var int32Val);
                    Console.WriteLine($"  +0x{offset:X2} (0x{(objAddr + (ulong)offset):X12}): 0x{val64:X16} (int32: {int32Val,12:N0}) {extra}");
                }
            }
        }
    }
}

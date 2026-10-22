using System;
using System.Collections.Generic;
using System.IO;
using OneImage.Text;

/// <summary>The installed-font scanner's header reader on arbitrary bytes: it may only report "not a font".</summary>
internal static class SysFontTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Targets.Guard(() =>
        {
            using var ms = new MemoryStream(bytes);
            _ = SystemFontSet.TryReadFamilies(ms, new List<string>());
        });
    }
}

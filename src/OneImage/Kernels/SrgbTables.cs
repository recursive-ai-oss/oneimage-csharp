namespace OneImage.Kernels;

/// <summary>
/// Lookup tables for resampling in linear light, built from the shared <see cref="SrgbTransfer"/> curve: 8- and 16-bit decode
/// tables and a 65536-entry table from linear 16-bit to the nearest 8-bit code. Untagged RGB and gray are treated as sRGB.
/// </summary>
internal static class SrgbTables
{
    // Entries of the 16-bit table where 1.10.2 and earlier had the value the Linux x64 math library gave, which is one unit in the
    // last place from the correctly rounded float in these 54 places. Kept so that results do not change; the rest of the table is
    // the exact curve evaluated without the platform math library, so the table has the same bits on every platform.
    private static readonly int[] s_patchIndex =
    [
        3142, 4862, 5117, 6811, 7176, 8122, 8580, 9883, 10355, 11618,
        14073, 15895, 16062, 17054, 18120, 23097, 23772, 23853, 25025, 25271,
        25822, 26932, 28595, 29291, 29857, 30128, 31659, 33214, 34522, 35028,
        35407, 36756, 37793, 38417, 41102, 41908, 43855, 47587, 52778, 53234,
        55461, 56073, 57259, 57757, 57778, 58068, 59291, 61491, 62358, 62367,
        62877, 63286, 64701, 64736,
    ];

    private static readonly uint[] s_patchBits =
    [
        0x3B75FD13, 0x3BD41F8F, 0x3BE3C803, 0x3C2E61EB, 0x3C3D691D, 0x3C67C7A1,
        0x3C7E1A70, 0x3CA222A8, 0x3CB01694, 0x3CD8C66D, 0x3D1B2CFB, 0x3D445E2E,
        0x3D486DA6, 0x3D618DF3, 0x3D7E80F4, 0x3DD0C574, 0x3DDDA99F, 0x3DDF3D64,
        0x3DF6CBD5, 0x3DFBEA93, 0x3E03CDCB, 0x3E100D87, 0x3E239A98, 0x3E2C384C,
        0x3E336AE0, 0x3E36ECB5, 0x3E4B7CB4, 0x3E61B0F4, 0x3E756A30, 0x3E7D4DF2,
        0x3E81A79F, 0x3E8CAD38, 0x3E958207, 0x3E9AF945, 0x3EB3CF9A, 0x3EBBB089,
        0x3ECF8A47, 0x3EF8E2F9, 0x3F1CE846, 0x3F1FF85F, 0x3F2F6D7F, 0x3F33D262,
        0x3F3C84B1, 0x3F403DD6, 0x3F406645, 0x3F42969A, 0x3F4BFA70, 0x3F5D85F4,
        0x3F64ABA3, 0x3F64BECF, 0x3F690329, 0x3F6C77B5, 0x3F78A6BB, 0x3F78F50A,
    ];

    /// <summary>Linear value of encoded <c>i / 255</c>; the same values as <see cref="SrgbTransfer"/>'s baked 8-bit decode table.</summary>
    public static readonly float[] ToLinear8 = SrgbTransfer.Decode8Table;

    /// <summary>Encoded value <c>i / 255</c> itself (used when resampling in the encoded space).</summary>
    public static readonly float[] Identity8 = BuildIdentity();

    /// <summary>Linear value of encoded <c>i / 65535</c>.</summary>
    public static readonly float[] ToLinear16 = BuildToLinear16();

    /// <summary>Nearest 8-bit sRGB code for linear <c>i / 65535</c>.</summary>
    public static readonly byte[] FromLinear8 = BuildFromLinear8();

    /// <summary>sRGB decode of one value; see <see cref="SrgbTransfer.DecodeOne"/>.</summary>
    public static float Decode(float v) => SrgbTransfer.DecodeOne(v);

    /// <summary>sRGB encode of one linear value; see <see cref="SrgbTransfer.EncodeOne"/>.</summary>
    public static float Encode(float v) => SrgbTransfer.EncodeOne(v);

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static float[] BuildToLinear16()
    {
        var t = new float[65536];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = SrgbTransfer.DecodeOne((float)i / 65535);
        }

        for (int k = 0; k < s_patchIndex.Length; k++)
        {
            t[s_patchIndex[k]] = BitConverter.UInt32BitsToSingle(s_patchBits[k]);
        }

        return t;
    }

    private static float[] BuildIdentity()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = i / 255f;
        }

        return t;
    }

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static byte[] BuildFromLinear8()
    {
        var t = new byte[65536];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = (byte)Math.Clamp((int)Math.Round(SrgbTransfer.EncodeOne((float)i / 65535) * 255, MidpointRounding.AwayFromZero), 0, 255);
        }

        return t;
    }
}

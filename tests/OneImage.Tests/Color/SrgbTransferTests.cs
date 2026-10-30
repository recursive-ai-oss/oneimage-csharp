using System.Buffers.Binary;
using System.Security.Cryptography;
using OneImage.Kernels;

namespace OneImage.Tests;

/// <summary>
/// The colour transfer tables and single-value functions must give the same bits on every platform, so the expected values here
/// are hashes that every operating system and architecture in CI checks. They were taken from the Linux x64 results of 1.10.2,
/// whose platform math library the tables are kept equal to.
/// </summary>
public class SrgbTransferTests
{
    // SHA-256 of the tables' little-endian bits.
    private const string Decode8Hash = "305F395A99E83B5E2F75B6DE7C72ACC42AF192EAB6E377A18D6B209BEEAD7C04";
    private const string EncodeHash = "DF48C855BE3696B88707C69F0EC550560A0B83BE8BD0BAF0781A39EFBDD7D709";
    private const string ToLinear16Hash = "4FE51821D929AEE3263E71790FA05DCF6B113EF31F96461A29D09F987F4CA247";
    private const string FromLinear8Hash = "02387F3D0FB18F89BD923D54ABCCEBC15685F8587AC91B4F4E2DDD8B3E953B83";

    // SHA-256 of DecodeOne and EncodeOne over a fixed dense sweep of [0, 1] (every 4099th float bit pattern) and a few specials.
    private const string SingleValueHash = "F1A4BEDE3820DCFAC1F23601D89ADFA3ED814A035E54EA903C33A130F96DC627";

    // SHA-256 of Pow over the gamma and sRGB exponents on the 8- and 16-bit input domains, and of Cbrt over a fixed sweep.
    private const string PowDoubleHash = "881CBCB331A90DF9D660A4CEFA2B942EFD8EC066F057BFB7733DD8E81C3AB400";
    private const string CbrtHash = "5764E640A2E43D291C92D3CDFBED75148762B212478F58D411379C336C34FC7E";

    private static string Hash(float[] t)
    {
        var bytes = new byte[t.Length * 4];
        for (int i = 0; i < t.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), t[i]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static int Ulps(float a, float b) => Math.Abs(BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b));

    [Fact]
    public void TablesHaveTheSameBitsOnEveryPlatform()
    {
        Assert.Equal(256, SrgbTransfer.Decode8Table.Length);
        Assert.Equal(16385, SrgbTransfer.EncodeTable.Length);
        Assert.Equal(Decode8Hash, Hash(SrgbTransfer.Decode8Table));
        Assert.Equal(EncodeHash, Hash(SrgbTransfer.EncodeTable));
    }

    [Fact]
    public void LinearLightResampleTablesHaveTheSameBitsOnEveryPlatform()
    {
        Assert.Equal(Hash(SrgbTransfer.Decode8Table), Hash(SrgbTables.ToLinear8));
        Assert.Equal(65536, SrgbTables.ToLinear16.Length);
        Assert.Equal(ToLinear16Hash, Hash(SrgbTables.ToLinear16));
        Assert.Equal(FromLinear8Hash, Convert.ToHexString(SHA256.HashData(SrgbTables.FromLinear8)));
    }

    [Fact]
    public void SingleValueFunctionsGiveTheSameBitsOnEveryPlatform()
    {
        var values = new List<float>();
        for (int bits = 0; bits <= 0x3F800000; bits += 4099)
        {
            float v = BitConverter.Int32BitsToSingle(bits);
            values.Add(SrgbTransfer.DecodeOne(v));
            values.Add(SrgbTransfer.EncodeOne(v));
        }

        foreach (float v in new[] { 1f, -0.5f, 0.04045f, 0.0031308f, 2f, 1e-30f })
        {
            values.Add(SrgbTransfer.DecodeOne(v));
            values.Add(SrgbTransfer.EncodeOne(v));
        }

        Assert.Equal(SingleValueHash, Hash([.. values]));
    }

    [Fact]
    public void FastAndExactPowerPathsAgree()
    {
        // The fast path is only allowed to return what the exact path returns; sweep the sRGB arguments finely.
        for (int bits = 0x3B000000; bits <= 0x3F800000; bits += 257)
        {
            float v = BitConverter.Int32BitsToSingle(bits);
            float x = (v + 0.055f) / 1.055f;
            Assert.Equal(DeterministicMath.PowFReference(x, 2.4f), DeterministicMath.PowF(x, 2.4f));
            Assert.Equal(DeterministicMath.PowFReference(v, 1f / 2.4f), DeterministicMath.PowF(v, 1f / 2.4f));
        }
    }

    [Fact]
    public void PowerAndCubeRootFollowTheCRulesForSpecialValues()
    {
        Assert.Equal(1f, DeterministicMath.PowF(float.NaN, 0f));
        Assert.True(float.IsNaN(DeterministicMath.PowF(-2f, 0.5f)));
        Assert.Equal(0f, DeterministicMath.PowF(0f, 2.4f));
        Assert.True(float.IsPositiveInfinity(DeterministicMath.PowF(0f, -1f)));
        Assert.True(float.IsPositiveInfinity(DeterministicMath.PowF(float.PositiveInfinity, 2.4f)));
        Assert.Equal(-8f, DeterministicMath.PowF(-2f, 3f));
        Assert.Equal(4f, DeterministicMath.PowF(-2f, 2f));
        Assert.Equal(8.0, DeterministicMath.Pow(2.0, 3.0));
        Assert.Equal(0.5, DeterministicMath.Pow(2.0, -1.0));
        Assert.True(float.IsPositiveInfinity(DeterministicMath.PowF(1e30f, 3f)));
        Assert.Equal(0f, DeterministicMath.PowF(1e-30f, 3f));
        Assert.Equal(3.0, DeterministicMath.Cbrt(27.0));
        Assert.Equal(-2f, DeterministicMath.CbrtF(-8f));
        Assert.Equal(0f, DeterministicMath.CbrtF(0f));
        Assert.True(float.IsNaN(DeterministicMath.CbrtF(float.NaN)));
    }

    [Fact]
    public void PowerAndCubeRootGiveTheSameBitsOnEveryPlatform()
    {
        double[] gammas = [1.0 / 3, 0.45, 0.5, 1 / 2.4, 1 / 2.2, 1.0, 1.8, 2.0, 2.2, 2.4, 2.6, 3.0, 1.0 / 1.8, 1.0 / 2.6, 5.0 / 3, 0.9, 1.1, 4.0 / 3];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[8];
        foreach (double g in gammas)
        {
            foreach (int den in new[] { 255, 65535 })
            {
                for (int i = 1; i <= den; i += den == 255 ? 1 : 7)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(buf, DeterministicMath.Pow((double)i / den, g));
                    sha.AppendData(buf);
                }
            }
        }

        for (int i = -400; i <= 400; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buf, DeterministicMath.Pow(2.0, i / 100.0));
            sha.AppendData(buf);
        }

        Assert.Equal(PowDoubleHash, Convert.ToHexString(sha.GetHashAndReset()));

        var buf4 = new byte[4];
        for (int bits = BitConverter.SingleToInt32Bits(0.0008f); bits < BitConverter.SingleToInt32Bits(4f); bits += 5003)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf4, DeterministicMath.CbrtF(BitConverter.Int32BitsToSingle(bits)));
            sha.AppendData(buf4);
        }

        Assert.Equal(CbrtHash, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>The platform math library may differ by one unit in the last place, never more: a sanity check that runs everywhere.</summary>
    [Fact]
    public void TablesAndFunctionsAreWithinOneUlpOfThePlatformFormula()
    {
        static float Dec(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        static float Enc(float v) => v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;

        for (int i = 0; i < 256; i++)
        {
            Assert.InRange(Ulps(SrgbTransfer.Decode8Table[i], Dec(i / 255f)), 0, 1);
        }

        for (int i = 0; i <= 16384; i++)
        {
            // Encode is 1.055 * pow - 0.055, which amplifies a one-ulp difference of the power by up to a few ulps.
            Assert.InRange(Ulps(SrgbTransfer.EncodeTable[i], Enc(i / 16384f)), 0, 4);
        }

        for (int i = 0; i < 65536; i++)
        {
            Assert.InRange(Ulps(SrgbTables.ToLinear16[i], Dec(i / 65535f)), 0, 1);
        }

        for (int bits = 0x3A000000; bits <= 0x3F800000; bits += 9973)
        {
            float v = BitConverter.Int32BitsToSingle(bits);
            Assert.InRange(Ulps(SrgbTransfer.DecodeOne(v), Dec(v)), 0, 1);
            Assert.InRange(Ulps(SrgbTransfer.EncodeOne(v), Enc(v)), 0, 4);
            // Platform cube roots are not all correctly rounded (the Windows one is off by up to 2 ulps), so allow 2.
            Assert.InRange(Ulps(DeterministicMath.CbrtF(v), MathF.Cbrt(v)), 0, 2);
            Assert.InRange(Ulps(DeterministicMath.PowF(v, 0.45f), MathF.Pow(v, 0.45f)), 0, 1);
            double d = Math.Pow(v, 2.2);
            Assert.True(Math.Abs(DeterministicMath.Pow(v, 2.2) - d) <= Math.Abs(d) * 4.5e-16);
        }
    }

    /// <summary>On the machine the tables were taken from, the formula with its math library still gives the baked decode table.</summary>
    [Fact]
    public void BakedTablesEqualTheFormulaOnLinuxX64()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        var t8 = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            t8[i] = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        var te = new float[16385];
        for (int i = 0; i <= 16384; i++)
        {
            float v = i / 16384f;
            te[i] = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
        }

        var t16 = new float[65536];
        var b8 = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            float v = i / 65535f;
            t16[i] = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            float e = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
            b8[i] = (byte)Math.Clamp((int)Math.Round(e * 255, MidpointRounding.AwayFromZero), 0, 255);
        }

        Assert.Equal(Decode8Hash, Hash(t8));
        Assert.Equal(EncodeHash, Hash(te));
        Assert.Equal(ToLinear16Hash, Hash(t16));
        Assert.Equal(FromLinear8Hash, Convert.ToHexString(SHA256.HashData(b8)));
    }
}

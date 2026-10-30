using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OneImage.Tests;

public class SrgbTransferTests
{
    // SHA-256 of the tables' little-endian float bits, as computed by the formula on linux-x64 in 1.10.2.
    private const string Decode8Hash = "305F395A99E83B5E2F75B6DE7C72ACC42AF192EAB6E377A18D6B209BEEAD7C04";
    private const string EncodeHash = "DF48C855BE3696B88707C69F0EC550560A0B83BE8BD0BAF0781A39EFBDD7D709";

    private static string Hash(float[] t)
    {
        var bytes = new byte[t.Length * 4];
        for (int i = 0; i < t.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), t[i]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Runs on every platform: the tables must have the same bits everywhere.</summary>
    [Fact]
    public void TablesHaveTheSameBitsOnEveryPlatform()
    {
        Assert.Equal(256, SrgbTransfer.Decode8Table.Length);
        Assert.Equal(16385, SrgbTransfer.EncodeTable.Length);
        Assert.Equal(Decode8Hash, Hash(SrgbTransfer.Decode8Table));
        Assert.Equal(EncodeHash, Hash(SrgbTransfer.EncodeTable));
    }

    /// <summary>The baked data is what the formula gives with the Linux x64 math library (others may differ by an ulp).</summary>
    [Fact]
    public void TablesEqualTheFormulaOnLinuxX64()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        Assert.Equal(Decode8Hash, Hash(SrgbTransfer.BuildDecode8()));
        Assert.Equal(EncodeHash, Hash(SrgbTransfer.BuildEncode()));
    }
}

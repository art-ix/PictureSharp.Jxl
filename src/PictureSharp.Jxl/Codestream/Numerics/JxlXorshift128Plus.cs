namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// Eight independent xorshift128+ generators seeded by SplitMix64, the random source of the noise stage. Ported from
/// libjxl v0.12.0 lib/jxl/xorshift128plus-inl.h (BSD-3-Clause); the lanes are independent, so the scalar loop gives the
/// same bits as libjxl's vector code.
/// </summary>
internal sealed partial class JxlXorshift128Plus
{
    public const int Lanes = 8;

    private readonly ulong[] s0 = new ulong[Lanes];
    private readonly ulong[] s1 = new ulong[Lanes];

    public JxlXorshift128Plus(uint seed1, uint seed2, uint seed3, uint seed4)
    {
        unchecked
        {
            s0[0] = SplitMix64(((ulong)seed1 << 32) + seed2 + 0x9E3779B97F4A7C15ul);
            s1[0] = SplitMix64(((ulong)seed3 << 32) + seed4 + 0x9E3779B97F4A7C15ul);
            for (int i = 1; i < Lanes; i++)
            {
                s0[i] = SplitMix64(s0[i - 1]);
                s1[i] = SplitMix64(s1[i - 1]);
            }
        }
    }

    /// <summary>Writes the next 64 random bits of every lane to <paramref name="bits"/>.</summary>
    public void Fill(Span<ulong> bits)
    {
        bits = bits[..Lanes];
        if (FillAvx2(bits)) return;

        unchecked
        {
            for (int i = 0; i < Lanes; i++)
            {
                ulong first = s0[i];
                ulong second = s1[i];
                bits[i] = first + second;
                s0[i] = second;
                first ^= first << 23;
                s1[i] = first ^ second ^ (first >> 18) ^ (second >> 5);
            }
        }
    }

    private static ulong SplitMix64(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
            return z ^ (z >> 31);
        }
    }
}

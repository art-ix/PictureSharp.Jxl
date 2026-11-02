using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

// Two vectors of four independent lanes with the same 64-bit operations per lane as the scalar loop.
internal sealed partial class JxlXorshift128Plus
{
    private bool FillAvx2(Span<ulong> bits)
    {
        if (!Avx2.IsSupported) return false;
        for (int i = 0; i < Lanes; i += 4)
        {
            Vector256<ulong> first = Vector256.Create(s0.AsSpan(i, 4));
            Vector256<ulong> second = Vector256.Create(s1.AsSpan(i, 4));
            (first + second).CopyTo(bits.Slice(i, 4));
            second.CopyTo(s0.AsSpan(i, 4));
            first ^= first << 23;
            (first ^ second ^ (first >>> 18) ^ (second >>> 5)).CopyTo(s1.AsSpan(i, 4));
        }

        return true;
    }
}

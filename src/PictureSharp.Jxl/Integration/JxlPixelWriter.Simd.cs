using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Integration;

// Output conversion has deliberately separate multiply/add instructions and needs no FMA.
internal static partial class JxlPixelWriter
{
    internal static int WriteWideRowSimd(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> row, bool gray)
    {
        if (Avx2.IsSupported) return WriteWideRowAvx2(r, g, b, a, row, gray);
        return Sse41.IsSupported ? WriteWideRowSse41(r, g, b, a, row, gray) : 0;
    }

    internal static int WriteWideRowAvx2(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> row, bool gray)
    {
        int end = row.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            Vector128<ushort> vr = WideAvx2(Vector256.Create(r.Slice(i, 8)));
            Vector128<ushort> vg = gray ? vr : WideAvx2(Vector256.Create(g.Slice(i, 8)));
            Vector128<ushort> vb = gray ? vr : WideAvx2(Vector256.Create(b.Slice(i, 8)));
            Vector128<ushort> va = a.IsEmpty ? Vector128.Create(ushort.MaxValue) : WideAvx2(Vector256.Create(a.Slice(i, 8)));
            StoreWide4(vr, vg, vb, va, row.Slice(i, 4));
            StoreWide4(Sse2.ShiftRightLogical128BitLane(vr, 8), Sse2.ShiftRightLogical128BitLane(vg, 8),
                Sse2.ShiftRightLogical128BitLane(vb, 8), Sse2.ShiftRightLogical128BitLane(va, 8), row.Slice(i + 4, 4));
        }
        return end;
    }

    internal static int WriteWideRowSse41(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> row, bool gray)
    {
        int end = row.Length & ~3;
        for (int i = 0; i < end; i += 4)
        {
            Vector128<ushort> vr = WideSse41(Vector128.Create(r.Slice(i, 4)));
            Vector128<ushort> vg = gray ? vr : WideSse41(Vector128.Create(g.Slice(i, 4)));
            Vector128<ushort> vb = gray ? vr : WideSse41(Vector128.Create(b.Slice(i, 4)));
            Vector128<ushort> va = a.IsEmpty ? Vector128.Create(ushort.MaxValue) : WideSse41(Vector128.Create(a.Slice(i, 4)));
            StoreWide4(vr, vg, vb, va, row.Slice(i, 4));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> WideAvx2(Vector256<float> value)
    {
        Vector256<float> rounded = Avx.Floor(Avx.Add(Avx.Multiply(value, Vector256.Create(65535.0f)), Vector256.Create(0.5f)));
        // NaN converts to zero like the scalar ushort conversion; infinities clamp to the endpoints.
        rounded = Avx.Min(Avx.Max(rounded, Vector256<float>.Zero), Vector256.Create(65535.0f));
        Vector256<int> integers = Avx.ConvertToVector256Int32WithTruncation(rounded);
        return Sse41.PackUnsignedSaturate(integers.GetLower(), integers.GetUpper());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> WideSse41(Vector128<float> value)
    {
        Vector128<float> rounded = Sse41.Floor(Sse.Add(Sse.Multiply(value, Vector128.Create(65535.0f)), Vector128.Create(0.5f)));
        rounded = Sse.Min(Sse.Max(rounded, Vector128<float>.Zero), Vector128.Create(65535.0f));
        return Sse41.PackUnsignedSaturate(Sse2.ConvertToVector128Int32WithTruncation(rounded), Vector128<int>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreWide4(Vector128<ushort> r, Vector128<ushort> g, Vector128<ushort> b, Vector128<ushort> a, Span<Rgba64> row)
    {
        Vector128<uint> rg = Sse2.UnpackLow(r, g).AsUInt32(), ba = Sse2.UnpackLow(b, a).AsUInt32();
        Span<ushort> output = MemoryMarshal.Cast<Rgba64, ushort>(row);
        Sse2.UnpackLow(rg, ba).AsUInt16().CopyTo(output);
        Sse2.UnpackHigh(rg, ba).AsUInt16().CopyTo(output[8..]);
    }
    internal static int WriteNarrowRowSimd(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba32> row, bool gray, int y)
    {
        if (Avx2.IsSupported) return WriteNarrowRowAvx2(r, g, b, a, row, gray, y);
        return Sse41.IsSupported ? WriteNarrowRowSse41(r, g, b, a, row, gray, y) : 0;
    }

    internal static int WriteNarrowRowAvx2(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba32> row, bool gray, int y)
    {
        int end = row.Length & ~7;
        ReadOnlySpan<float> dither = DitherSimd.Rows.AsSpan((y & 31) * 128, 128);
        Span<uint> output = MemoryMarshal.Cast<Rgba32, uint>(row);
        for (int x = 0; x < end; x += 8)
        {
            int d = x & 31;
            Vector256<uint> vr = NarrowAvx2(Vector256.Create(r.Slice(x, 8)), Vector256.Create(dither.Slice(d, 8)));
            Vector256<uint> vg = gray ? vr : NarrowAvx2(Vector256.Create(g.Slice(x, 8)), Vector256.Create(dither.Slice(32 + d, 8)));
            Vector256<uint> vb = gray ? vr : NarrowAvx2(Vector256.Create(b.Slice(x, 8)), Vector256.Create(dither.Slice(64 + d, 8)));
            Vector256<uint> va = a.IsEmpty ? Vector256.Create(255u)
                : NarrowAvx2(Vector256.Create(a.Slice(x, 8)), Vector256.Create(dither.Slice((gray ? 32 : 96) + d, 8)));
            (vr | Avx2.ShiftLeftLogical(vg, 8) | Avx2.ShiftLeftLogical(vb, 16) | Avx2.ShiftLeftLogical(va, 24)).CopyTo(output.Slice(x, 8));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> NarrowAvx2(Vector256<float> value, Vector256<float> dither)
    {
        Vector256<float> scaled = Avx.Add(Avx.Multiply(value, Vector256.Create(255.0f)), dither);
        scaled = Avx.Min(Avx.Max(scaled, Vector256<float>.Zero), Vector256.Create(255.0f));
        return Avx.ConvertToVector256Int32WithTruncation(Avx.RoundToNearestInteger(scaled)).AsUInt32();
    }
    internal static int WriteNarrowRowSse41(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba32> row, bool gray, int y)
    {
        int end = row.Length & ~3;
        ReadOnlySpan<float> dither = DitherSimd.Rows.AsSpan((y & 31) * 128, 128);
        Span<uint> output = MemoryMarshal.Cast<Rgba32, uint>(row);
        for (int x = 0; x < end; x += 4)
        {
            int d = x & 31;
            Vector128<uint> vr = NarrowSse41(Vector128.Create(r.Slice(x, 4)), Vector128.Create(dither.Slice(d, 4)));
            Vector128<uint> vg = gray ? vr : NarrowSse41(Vector128.Create(g.Slice(x, 4)), Vector128.Create(dither.Slice(32 + d, 4)));
            Vector128<uint> vb = gray ? vr : NarrowSse41(Vector128.Create(b.Slice(x, 4)), Vector128.Create(dither.Slice(64 + d, 4)));
            Vector128<uint> va = a.IsEmpty ? Vector128.Create(255u)
                : NarrowSse41(Vector128.Create(a.Slice(x, 4)), Vector128.Create(dither.Slice((gray ? 32 : 96) + d, 4)));
            (vr | Sse2.ShiftLeftLogical(vg, 8) | Sse2.ShiftLeftLogical(vb, 16) | Sse2.ShiftLeftLogical(va, 24)).CopyTo(output.Slice(x, 4));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> NarrowSse41(Vector128<float> value, Vector128<float> dither)
    {
        Vector128<float> scaled = Sse.Add(Sse.Multiply(value, Vector128.Create(255.0f)), dither);
        scaled = Sse.Min(Sse.Max(scaled, Vector128<float>.Zero), Vector128.Create(255.0f));
        return Sse2.ConvertToVector128Int32WithTruncation(Sse41.RoundToNearestInteger(scaled)).AsUInt32();
    }

    // Fixed 16 KiB lookup: channel phases and the 32-column wrap, allocated once on first float output.
    private static class DitherSimd
    {
        internal static readonly float[] Rows = CreateRows();

        private static float[] CreateRows()
        {
            float[] rows = new float[32 * 128];
            for (int y = 0; y < 32; y++)
            for (int c = 0; c < 4; c++)
            for (int x = 0; x < 32; x++)
                rows[y * 128 + c * 32 + x] = Codestream.Color.JxlDither.Row(y, c)[Codestream.Color.JxlDither.Column(x, c)];
            return rows;
        }
    }

}

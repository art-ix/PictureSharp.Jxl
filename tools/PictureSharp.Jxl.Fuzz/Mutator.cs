using System.Buffers.Binary;

namespace PictureSharp.Formats.Jxl.Fuzz
{
    using PictureSharp.Formats.Jxl.Codestream.IO;

    /// <summary>xoshiro256**, seeded with SplitMix64: the same inputs on every runtime and machine.</summary>
    public sealed class FuzzRandom
    {
        private ulong s0, s1, s2, s3;

        public FuzzRandom(ulong seed, ulong stream)
        {
            ulong x = seed * 0x9E3779B97F4A7C15UL ^ stream;
            s0 = SplitMix(ref x); s1 = SplitMix(ref x); s2 = SplitMix(ref x); s3 = SplitMix(ref x);
        }

        private static ulong SplitMix(ref ulong x)
        {
            ulong z = x += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong Next()
        {
            ulong result = ulong.RotateLeft(s1 * 5, 7) * 9;
            ulong t = s1 << 17;
            s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t; s3 = ulong.RotateLeft(s3, 45);
            return result;
        }

        /// <summary>A value in [0, <paramref name="bound"/>).</summary>
        public int Below(int bound) => bound <= 1 ? 0 : (int)(Next() % (ulong)bound);

        public bool Chance(int percent) => Below(100) < percent;
    }

    /// <summary>A seed input; codestream fields come from a trace of its reads (raw or single-jxlc files).</summary>
    public sealed class FuzzSeed(string name, byte[] data)
    {
        public string Name => name;

        public byte[] Data => data;

        /// <summary>Bit offsets in the file of the first fields the decoder read, with their widths.</summary>
        public (long Bit, int Count)[] Fields { get; private set; } = [];

        /// <summary>Records the fields <paramref name="decode"/> reads; fields across a part boundary are left out.</summary>
        internal void TraceFields(Action decode)
        {
            List<(long Codestream, int File, int Length)> segments = Mutator.CodestreamSegments(data);
            if (segments.Count == 0) return;
            var fields = new List<(long, int)>();
            foreach ((long bit, int count) in JxlFuzzTrace.Record(decode))
            {
                foreach ((long codestream, int file, int length) in segments)
                {
                    long first = bit - codestream * 8;
                    if (first >= 0 && first + count <= length * 8L) fields.Add((file * 8L + first, count));
                }
            }

            Fields = [.. fields];
        }
    }

    /// <summary>Derives one input from (seed, iteration) by applying one to four mutations to a seed.</summary>
    public static class Mutator
    {
        public const int MaxLength = 1 << 20;

        private static readonly byte[] Boundary = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];

        public static byte[] Generate(IReadOnlyList<FuzzSeed> seeds, ulong seed, long iteration)
        {
            var random = new FuzzRandom(seed, (ulong)iteration);
            FuzzSeed source = seeds[random.Below(seeds.Count)];
            var data = new List<byte>(source.Data);
            int count = 1 + random.Below(4);
            // Field positions hold only before the length changes, so fields go first.
            if (source.Fields.Length > 0 && random.Chance(40))
            {
                for (int i = random.Below(2); i >= 0; i--, count--) MutateField(data, source.Fields[random.Below(source.Fields.Length)], random);
            }

            for (int i = 0; i < count; i++) Mutate(data, seeds, random);
            if (data.Count > MaxLength) data.RemoveRange(MaxLength, data.Count - MaxLength);
            return [.. data];
        }

        private static void Mutate(List<byte> data, IReadOnlyList<FuzzSeed> seeds, FuzzRandom random)
        {
            if (data.Count == 0)
            {
                data.Add((byte)random.Next());
                return;
            }

            int at = random.Below(data.Count);
            switch (random.Below(IsBoxed(data) ? 10 : 9))
            {
                case 0: case 1: data[at] ^= (byte)(1 << random.Below(8)); break;
                case 2: data[at] = random.Chance(70) ? Boundary[random.Below(Boundary.Length)] : (byte)random.Next(); break;
                case 3: data[at] = unchecked((byte)(data[at] + random.Below(33) - 16)); break;
                case 4:
                    {
                        int length = 1 + random.Below(64);
                        data.InsertRange(at, Enumerable.Range(0, length).Select(_ => random.Chance(50) ? (byte)0 : (byte)random.Next()));
                        break;
                    }
                case 5: data.RemoveRange(at, Math.Min(data.Count - at, 1 + random.Below(Math.Max(1, data.Count / 8)))); break;
                case 6:
                    {
                        // A copy of a range of this input, repeated one to four times.
                        int length = Math.Min(data.Count - at, 1 + random.Below(256));
                        byte[] range = [.. data.GetRange(at, length)];
                        int target = random.Below(data.Count + 1);
                        for (int i = random.Below(4); i >= 0; i--) data.InsertRange(target, range);
                        break;
                    }
                case 7: data.RemoveRange(at, data.Count - at); break;
                case 8:
                    {
                        // Splice a range of another seed over or into this input.
                        byte[] other = seeds[random.Below(seeds.Count)].Data;
                        if (other.Length == 0) break;
                        int from = random.Below(other.Length);
                        int length = Math.Min(other.Length - from, 1 + random.Below(1024));
                        if (random.Chance(50)) data.RemoveRange(at, Math.Min(length, data.Count - at));
                        data.InsertRange(at, other.AsSpan(from, length).ToArray());
                        break;
                    }
                default: MutateBox(data, random); break;
            }
        }

        private static void MutateField(List<byte> data, (long Bit, int Count) field, FuzzRandom random)
        {
            ulong max = field.Count >= 64 ? ulong.MaxValue : (1UL << field.Count) - 1;
            ulong value = random.Below(5) switch
            {
                0 => 0,
                1 => max,
                2 => 1,
                3 => ReadBits(data, field.Bit, field.Count) ^ (1UL << random.Below(field.Count)),
                _ => random.Next() & max,
            };
            // Fields are least significant bit first, as the bit reader reads them.
            for (int i = 0; i < field.Count; i++)
            {
                long bit = field.Bit + i;
                if (bit / 8 >= data.Count) return;
                int index = (int)(bit / 8);
                byte mask = (byte)(1 << (int)(bit % 8));
                data[index] = ((value >> i) & 1) != 0 ? (byte)(data[index] | mask) : (byte)(data[index] & ~mask);
            }
        }

        private static ulong ReadBits(List<byte> data, long start, int count)
        {
            ulong value = 0;
            for (int i = 0; i < count && (start + i) / 8 < data.Count; i++)
            {
                long bit = start + i;
                value |= (ulong)((data[(int)(bit / 8)] >> (int)(bit % 8)) & 1) << i;
            }

            return value;
        }

        private static void MutateBox(List<byte> data, FuzzRandom random)
        {
            byte[] bytes = [.. data];
            List<(int Start, int Length)> boxes = Boxes(bytes);
            if (boxes.Count == 0) return;
            (int start, int length) = boxes[random.Below(boxes.Count)];
            switch (random.Below(5))
            {
                case 0:
                    {
                        // Size field: 0 (to the end), 1 (64-bit size), too small, one off, or too large.
                        uint size = (uint)length;
                        uint[] choices = [0, 1, 7, 8, size - 1, size + 1, size * 2, uint.MaxValue];
                        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(start), choices[random.Below(choices.Length)]);
                        break;
                    }
                case 1:
                    {
                        // Swap with another box.
                        (int otherStart, int otherLength) = boxes[random.Below(boxes.Count)];
                        if (otherStart == start) return;
                        (int a, int aLength, int b, int bLength) = start < otherStart ? (start, length, otherStart, otherLength) : (otherStart, otherLength, start, length);
                        bytes = [.. bytes.AsSpan(0, a), .. bytes.AsSpan(b, bLength), .. bytes.AsSpan(a + aLength, b - a - aLength), .. bytes.AsSpan(a, aLength), .. bytes.AsSpan(b + bLength)];
                        break;
                    }
                case 2: bytes = [.. bytes.AsSpan(0, start + length), .. bytes.AsSpan(start, length), .. bytes.AsSpan(start + length)]; break;
                case 3: bytes = [.. bytes.AsSpan(0, start), .. bytes.AsSpan(start + length)]; break;
                default:
                    {
                        // The first payload word: jxlp counters, brob types, ftyp brands.
                        if (length < 12) return;
                        uint[] choices = [0, 1, 2, 0x80000000, 0x80000001, 0x7FFFFFFF, uint.MaxValue];
                        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(start + 8), choices[random.Below(choices.Length)]);
                        break;
                    }
            }

            data.Clear();
            data.AddRange(bytes);
        }

        private static readonly byte[] Signature = [0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

        private static bool IsBoxed(List<byte> data) => data.Count >= Signature.Length && data.Take(Signature.Length).SequenceEqual(Signature);

        /// <summary>The boxes of an ISO BMFF file up to the first that does not fit; offsets and total lengths.</summary>
        public static List<(int Start, int Length)> Boxes(ReadOnlySpan<byte> bytes)
        {
            var boxes = new List<(int, int)>();
            int at = 0;
            while (at + 8 <= bytes.Length)
            {
                ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
                if (size == 1)
                {
                    if (at + 16 > bytes.Length) break;
                    size = BinaryPrimitives.ReadUInt64BigEndian(bytes[(at + 8)..]);
                }
                else if (size == 0)
                {
                    size = (ulong)(bytes.Length - at);
                }

                if (size < 8 || size > (ulong)(bytes.Length - at)) break;
                boxes.Add((at, (int)size));
                at += (int)size;
            }

            return boxes;
        }

        /// <summary>
        /// Where the codestream lies in the file, as (codestream offset, file offset, length) in bytes: the whole file for a
        /// raw codestream, the jxlc payload, or the jxlp payloads in counter order. Empty when there is none.
        /// </summary>
        public static List<(long Codestream, int File, int Length)> CodestreamSegments(ReadOnlySpan<byte> bytes)
        {
            if (bytes is [0xFF, 0x0A, ..]) return [(0, 0, bytes.Length)];
            var parts = new List<(uint Counter, int File, int Length)>();
            foreach ((int start, int length) in Boxes(bytes))
            {
                int header = BinaryPrimitives.ReadUInt32BigEndian(bytes[start..]) == 1 ? 16 : 8;
                ReadOnlySpan<byte> type = bytes.Slice(start + 4, 4);
                if (type.SequenceEqual("jxlc"u8)) return [(0, start + header, length - header)];
                if (type.SequenceEqual("jxlp"u8) && length >= header + 4)
                    parts.Add((BinaryPrimitives.ReadUInt32BigEndian(bytes[(start + header)..]) & 0x7FFFFFFF, start + header + 4, length - header - 4));
            }

            var segments = new List<(long, int, int)>();
            long offset = 0;
            foreach ((_, int file, int partLength) in parts.OrderBy(part => part.Counter))
            {
                segments.Add((offset, file, partLength));
                offset += partLength;
            }

            return segments;
        }
    }
}

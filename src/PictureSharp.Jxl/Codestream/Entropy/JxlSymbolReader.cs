using System.Buffers;
using System.Runtime.CompilerServices;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>
/// Decodes hybrid integers of a <see cref="JxlEntropyCode"/>: one ANS state (or prefix codes) shared by all
/// contexts, and the LZ77 window. Ported from libjxl v0.12.0 lib/jxl/dec_ans.h (ANSSymbolReader), BSD-3-Clause.
/// </summary>
internal sealed class JxlSymbolReader : IDisposable
{
    private const int WindowSize = 1 << 20;
    private const int WindowMask = WindowSize - 1;

    // Special LZ77 distances (dx, dy) from WebP lossless, scaled by the row width.
    private static readonly sbyte[] SpecialDistances =
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3, 3, 1, -3, 1,
        2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4, 4, 2, -4, 2, 0, 5, 3, 4,
        -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2, 4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3,
        0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2, 4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3,
        0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1, 4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7,
        7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5, 8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7,
        7, 5, -7, 5, 8, 4, 6, 7, -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    private readonly JxlEntropyCode code;
    // Copies of the code's tables, read once per symbol on the hot path.
    private readonly byte[] contextMap;
    private readonly JxlHybridUint[] configs;
    private readonly JxlAliasEntry[] aliasTables;
    private readonly bool usePrefixCode;
    private readonly int logAlphaSize;
    private readonly int logEntrySize;
    private uint[]? window;
    private readonly ArrayPool<uint> windowPool;
    private bool disposed;
    private readonly uint[] specialDistances = [];
    private uint state = JxlEntropyCode.AnsSignature;
    private uint decoded;
    private uint toCopy;
    private uint copyPosition;

    /// <summary>Starts reading after the histograms; ANS streams begin with the 32-bit state.</summary>
    /// <remarks><paramref name="distanceMultiplier"/> is the row width for special LZ77 distances; 0 disables them.</remarks>
    public JxlSymbolReader(JxlEntropyCode code, ref JxlBitReader reader, int distanceMultiplier = 0, ArrayPool<uint>? windowPool = null)
    {
        this.code = code;
        contextMap = code.ContextMap;
        configs = code.Configs;
        aliasTables = code.AliasTables;
        usePrefixCode = code.UsePrefixCode;
        logAlphaSize = code.LogAlphaSize;
        this.windowPool = windowPool ?? JxlArrayPool<uint>.Shared;
        if (!code.UsePrefixCode)
        {
            state = reader.ReadBits(32);
            logEntrySize = JxlEntropyCode.AnsLogTabSize - code.LogAlphaSize;
        }

        if (code.Lz77Enabled)
        {
            if (distanceMultiplier != 0)
            {
                specialDistances = new uint[SpecialDistances.Length / 2];
                for (int i = 0; i < specialDistances.Length; i++)
                {
                    int distance = SpecialDistances[2 * i] + distanceMultiplier * SpecialDistances[2 * i + 1];
                    specialDistances[i] = (uint)Math.Max(distance, 1);
                }
            }
            window = this.windowPool.Rent(WindowSize);
        }
    }

    /// <summary>Whether the ANS state returned to its initial value, which every complete stream ends with.</summary>
    public bool IsFinalStateValid => state == JxlEntropyCode.AnsSignature;

    /// <summary>Reads the value of an unclustered context.</summary>
    public uint Read(int context, ref JxlBitReader reader) => ReadClustered(contextMap[context], ref reader);

    /// <summary>
    /// Reads <c>values.Length</c> values of one cluster, as repeated <see cref="ReadClustered"/> calls would: the
    /// prefix-code path skips the per-symbol dispatch and LZ77 copies run as one loop.
    /// </summary>
    public void ReadClusteredRow(int cluster, ref JxlBitReader reader, Span<uint> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (window is null && usePrefixCode)
        {
            code.PrefixCodes[cluster].ReadRow(ref reader, configs[cluster], uint.MaxValue, values, out _);
            return;
        }

        if (window is null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ReadClustered(cluster, ref reader);
            }

            return;
        }

        uint[] history = window;
        JxlHybridUint hybrid = configs[cluster];
        uint literalSplit = hybrid.SplitToken;
        JxlPrefixCode? literals = usePrefixCode ? code.PrefixCodes[cluster] : null;
        uint minSymbol = code.Lz77MinSymbol;
        int at = 0;
        while (at < values.Length)
        {
            if (toCopy == 0)
            {
                // Literals up to the next copy; the window position stays in a local meanwhile.
                uint position = decoded;
                if (literals is not null)
                {
                    int filled = literals.ReadRow(ref reader, hybrid, minSymbol, values[at..], out uint stop);
                    for (int end = at + filled; at < end; at++)
                    {
                        history[position++ & WindowMask] = values[at];
                    }

                    decoded = position;
                    if (at < values.Length)
                    {
                        StartCopy(stop, ref reader);
                    }

                    if (toCopy == 0)
                    {
                        continue;
                    }
                }

                while (literals is null && at < values.Length)
                {
                    uint token = ReadAnsSymbol(cluster, ref reader);
                    if (token >= minSymbol)
                    {
                        decoded = position;
                        StartCopy(token, ref reader);
                        break;
                    }

                    uint literal = token < literalSplit ? token : hybrid.Decode(token, ref reader);
                    history[position++ & WindowMask] = literal;
                    values[at++] = literal;
                }

                if (toCopy == 0)
                {
                    decoded = position;
                    continue;
                }
            }

            int count = (int)Math.Min(toCopy, (uint)(values.Length - at));
            uint from = copyPosition;
            uint to = decoded;
            for (int end = at + count; at < end; at++)
            {
                uint value = history[from++ & WindowMask];
                history[to++ & WindowMask] = value;
                values[at] = value;
            }

            copyPosition = from;
            decoded = to;
            toCopy -= (uint)count;
        }
    }

    /// <summary>Reads the value of a cluster (a context already mapped through the context map).</summary>
    public uint ReadClustered(int cluster, ref JxlBitReader reader)
    {
        if (window is null && !disposed)
        {
            uint symbol = usePrefixCode ? code.PrefixCodes[cluster].ReadSymbol(ref reader) : ReadAnsSymbol(cluster, ref reader);
            ref readonly JxlHybridUint config = ref configs[cluster];
            return symbol < config.SplitToken ? symbol : config.Decode(symbol, ref reader);
        }

        return ReadWindowed(cluster, ref reader);
    }

    /// <summary>The LZ77 path of <see cref="ReadClustered"/>, kept out of line so the common path inlines.</summary>
    private uint ReadWindowed(int cluster, ref JxlBitReader reader)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        uint[] window = this.window!;
        if (toCopy > 0)
        {
            return Copy();
        }

        uint token = ReadSymbol(cluster, ref reader);
        if (token < code.Lz77MinSymbol)
        {
            uint value = configs[cluster].Decode(token, ref reader);
            window[decoded++ & WindowMask] = value;
            return value;
        }

        StartCopy(token, ref reader);
        return Copy();
    }

    /// <summary>Starts an LZ77 copy for a length <paramref name="token"/>: reads its length and distance.</summary>
    private void StartCopy(uint token, ref JxlBitReader reader)
    {
        uint[] window = this.window!;
        toCopy = unchecked(code.Lz77LengthConfig.Decode(token - code.Lz77MinSymbol, ref reader) + code.Lz77MinLength);
        int distanceCluster = code.Lz77DistanceCluster;
        uint distance = configs[distanceCluster].Decode(ReadSymbol(distanceCluster, ref reader), ref reader);
        distance = distance < specialDistances.Length ? specialDistances[distance] : unchecked(distance + 1 - (uint)specialDistances.Length);
        distance = Math.Min(Math.Min(distance, decoded), WindowSize);
        copyPosition = decoded - distance;
        if (distance == 0)
        {
            window.AsSpan(0, (int)Math.Min(toCopy, WindowSize)).Clear();
        }

        if (toCopy < code.Lz77MinLength)
        {
            throw JxlDescriptor.Corrupt("Invalid LZ77 length.");
        }
    }

    private uint Copy()
    {
        uint value = window![copyPosition++ & WindowMask];
        toCopy--;
        window[decoded++ & WindowMask] = value;
        return value;
    }

    private uint ReadSymbol(int cluster, ref JxlBitReader reader) =>
        usePrefixCode ? code.PrefixCodes[cluster].ReadSymbol(ref reader) : ReadAnsSymbol(cluster, ref reader);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadAnsSymbol(int cluster, ref JxlBitReader reader)
    {
        uint slot = state & (JxlEntropyCode.AnsTabSize - 1);
        JxlAliasEntry entry = aliasTables[(cluster << logAlphaSize) + (int)(slot >> logEntrySize)];
        int position = (int)(slot & ((1u << logEntrySize) - 1));
        bool greater = position >= entry.Cutoff;
        uint symbol = (uint)(greater ? entry.RightValue : (int)(slot >> logEntrySize));
        uint offset = (uint)((greater ? entry.Offsets1 : 0) + position);
        uint frequency = (uint)(greater ? entry.Freq1 : entry.Freq0);
        state = unchecked(frequency * (state >> JxlEntropyCode.AnsLogTabSize) + offset);
        if (state < 1u << 16)
        {
            state = (state << 16) | reader.ReadBits(16);
        }

        return symbol;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (window is { } owned)
        {
            window = null;
            windowPool.Return(owned);
        }
    }
}


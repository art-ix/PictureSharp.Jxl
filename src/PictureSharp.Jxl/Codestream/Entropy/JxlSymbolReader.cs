using System.Buffers;
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
        this.windowPool = windowPool ?? ArrayPool<uint>.Shared;
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
    public uint Read(int context, ref JxlBitReader reader) => ReadClustered(code.ContextMap[context], ref reader);

    /// <summary>Reads the value of a cluster (a context already mapped through the context map).</summary>
    public uint ReadClustered(int cluster, ref JxlBitReader reader)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (window is null)
        {
            return code.Configs[cluster].Decode(ReadSymbol(cluster, ref reader), ref reader);
        }

        if (toCopy > 0)
        {
            return Copy();
        }

        uint token = ReadSymbol(cluster, ref reader);
        if (token < code.Lz77MinSymbol)
        {
            uint value = code.Configs[cluster].Decode(token, ref reader);
            window[decoded++ & WindowMask] = value;
            return value;
        }

        toCopy = unchecked(code.Lz77LengthConfig.Decode(token - code.Lz77MinSymbol, ref reader) + code.Lz77MinLength);
        int distanceCluster = code.Lz77DistanceCluster;
        uint distance = code.Configs[distanceCluster].Decode(ReadSymbol(distanceCluster, ref reader), ref reader);
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

        return Copy();
    }

    private uint Copy()
    {
        uint value = window![copyPosition++ & WindowMask];
        toCopy--;
        window[decoded++ & WindowMask] = value;
        return value;
    }

    private uint ReadSymbol(int cluster, ref JxlBitReader reader)
    {
        if (code.UsePrefixCode)
        {
            return code.PrefixCodes[cluster].ReadSymbol(ref reader);
        }

        uint slot = state & (JxlEntropyCode.AnsTabSize - 1);
        JxlAliasEntry entry = code.AliasTables[(cluster << code.LogAlphaSize) + (int)(slot >> logEntrySize)];
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

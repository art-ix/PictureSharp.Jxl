using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular.Transforms;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>A Modular image: channels (meta channels such as palettes first) and the transforms to undo.</summary>
internal sealed class JxlModularImage
{
    public JxlModularImage(int bitDepth, List<JxlChannel> channels)
    {
        BitDepth = bitDepth;
        this.channels = [.. channels];
        Channels = this.channels.AsReadOnly();
    }

    private readonly List<JxlChannel> channels;

    public IReadOnlyList<JxlChannel> Channels { get; }

    public int MetaChannels { get; set; }

    public int BitDepth { get; }

    private readonly List<IJxlModularTransform> transforms = [];

    public void ApplyTransforms(IReadOnlyList<JxlTransform> descriptions)
    {
        if (transforms.Count != 0) throw new InvalidOperationException("Transforms are already pending.");
        foreach (JxlTransform description in descriptions)
        {
            IJxlModularTransform transform = description.Id switch
            {
                JxlTransformId.Rct => new JxlRctTransform(description),
                JxlTransformId.Palette => new JxlPaletteTransform(description),
                JxlTransformId.Squeeze => new JxlSqueezeTransform(description),
                _ => throw JxlDescriptor.Corrupt("Invalid Modular transform.")
            };
            transform.MetaApply(this);
            transforms.Add(transform);
        }
    }

    public void UndoTransforms(JxlWeightedHeader weighted, CancellationToken cancellationToken = default)
    {
        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transforms[i].Inverse(this, weighted, cancellationToken);
        }
        transforms.Clear();
    }

    public void ReplaceChannel(int index, JxlChannel channel) => channels[index] = channel;
    public void InsertChannel(int index, JxlChannel channel) => channels.Insert(index, channel);
    public void InsertChannels(int index, IEnumerable<JxlChannel> values) => channels.InsertRange(index, values);
    public void RemoveChannels(int index, int count) => channels.RemoveRange(index, count);

    /// <summary>Fails unless channels <paramref name="first"/>..<paramref name="last"/> exist, have one shape and do not mix meta and data.</summary>
    public void CheckEqualChannels(long first, long last)
    {
        if (first > Channels.Count || last >= Channels.Count || last < first
            || (first < MetaChannels && last >= MetaChannels)
            || Channels.Skip((int)first + 1).Take((int)(last - first)).Any(channel => !channel.SameShape(Channels[(int)first])))
        {
            throw JxlDescriptor.Corrupt("Invalid channel range of a Modular transform.");
        }
    }
}

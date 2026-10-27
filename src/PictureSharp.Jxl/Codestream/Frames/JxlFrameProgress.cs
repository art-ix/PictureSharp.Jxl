namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Enforces LF/global/group/pass ordering and a single consumption of the completed frame.</summary>
internal sealed class JxlFrameProgress(long lfGroups, long groups, uint passes)
{
    private enum Stage { Created, Lf, LfComplete, Groups, Group, Complete, Consumed }
    private Stage stage;
    private int nextLf;
    private int nextGroup;
    private int nextPass;

    public void BeginLf() { Require(stage == Stage.Created); stage = Stage.Lf; }
    public void LfGroup(int group) { Require(stage == Stage.Lf && group == nextLf && group < lfGroups); nextLf++; }
    public void FinishLf() { Require(stage == Stage.Lf && nextLf == lfGroups); stage = Stage.LfComplete; }
    public void BeginHf() { Require(stage == Stage.LfComplete); stage = Stage.Groups; }
    public void BeginGroup(int group)
    {
        Require(stage == Stage.Groups && group == nextGroup && group < groups);
        nextPass = 0;
        stage = Stage.Group;
    }
    public void Pass(int group, int pass)
    {
        Require(stage == Stage.Group && group == nextGroup && pass == nextPass && pass < passes);
        nextPass++;
    }
    public void FinishGroup(int group)
    {
        Require(stage == Stage.Group && group == nextGroup && nextPass == passes);
        nextGroup++;
        stage = nextGroup == groups ? Stage.Complete : Stage.Groups;
    }
    public void Consume() { Require(stage == Stage.Complete); stage = Stage.Consumed; }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Frame sections were consumed out of order.");
    }
}

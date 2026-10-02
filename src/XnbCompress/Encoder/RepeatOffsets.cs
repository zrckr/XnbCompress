namespace XnbCompress.Encoder;

internal readonly struct RepeatOffsets
{
    public readonly int R0;
    public readonly int R1;
    public readonly int R2;

    public RepeatOffsets() : this(1, 1, 1)
    {
    }

    private RepeatOffsets(int r0, int r1, int r2)
    {
        R0 = r0;
        R1 = r1;
        R2 = r2;
    }

    public RepeatOffsets Apply(int encodedDistance)
    {
        return encodedDistance switch
        {
            0 => this,
            1 => new RepeatOffsets(R1, R0, R2),
            2 => new RepeatOffsets(R2, R1, R0),
            _ => new RepeatOffsets(encodedDistance - 2, R0, R1)
        };
    }
}

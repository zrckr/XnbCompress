namespace XnbCompress.Encoder;

// Distances borrow matcher storage and must be consumed before its next search
internal readonly struct MatchCandidates
{
    public int Length { get; }
    public int[] Distances { get; }

    public MatchCandidates(int length, int[] distances)
    {
        Length = length;
        Distances = distances;
    }
}

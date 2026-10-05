namespace XnbCompress.Encoder;

// Distances borrow matcher storage and must be consumed before its next search
// Only indices 2 through Length contain valid distances; other entries are unused.
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

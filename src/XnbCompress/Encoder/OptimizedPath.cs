using System.Collections.Generic;

namespace XnbCompress.Encoder;

// Tokens borrow optimizer storage and must be consumed before its next search
internal readonly struct OptimizedPath
{
    public IReadOnlyList<Token> Tokens { get; }
    public int NextPosition { get; }
    public RepeatOffsets Repeats { get; }

    public OptimizedPath(IReadOnlyList<Token> tokens, int nextPosition, RepeatOffsets repeats)
    {
        Tokens = tokens;
        NextPosition = nextPosition;
        Repeats = repeats;
    }
}

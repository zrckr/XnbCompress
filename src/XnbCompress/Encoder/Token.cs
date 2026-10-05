using System;

namespace XnbCompress.Encoder;

internal readonly struct Token
{
    public int DecodedLength => Kind == TokenKind.Literal ? 1 : MatchLength;
    public readonly TokenKind Kind;
    public readonly byte LiteralValue;
    public readonly int MatchLength;
    public readonly int EncodedDistance;

    private Token(TokenKind kind, byte literalValue, int matchLength, int encodedDistance)
    {
        Kind = kind;
        LiteralValue = literalValue;
        MatchLength = matchLength;
        EncodedDistance = encodedDistance;
    }

    public static Token Literal(byte value)
    {
        return new Token(TokenKind.Literal, value, 0, 0);
    }

    public static Token Match(int length, int encodedDistance)
    {
        return new Token(TokenKind.Match, 0, length, encodedDistance);
    }
}

internal enum TokenKind
{
    Literal,
    Match
}

internal static class TokenUtils
{
    public static int GetMainSymbol(Token token)
    {
        // Main-tree symbols 0..255 represent literal bytes
        if (token.Kind == TokenKind.Literal)
        {
            return token.LiteralValue;
        }

        var positionSlot = GetPositionSlot(token.EncodedDistance).Slot;
        return GetMainSymbol(token, positionSlot);
    }

    public static int GetMainSymbol(Token token, int positionSlot)
    {
        if (token.Kind == TokenKind.Literal)
        {
            return token.LiteralValue;
        }

        // Reuse the distance slot when its extra-bit count is also needed.
        // Length codes 0..6 cover 2..8; code 7 uses the secondary tree.
        var lengthCode = Math.Min(token.MatchLength - Constants.MinimumMatchLength, Constants.PrimaryLengthCount);
        return Constants.LiteralSymbolCount + Constants.LengthCodesPerSlot * positionSlot + lengthCode;
    }

    public static (int Slot, int ExtraBits) GetPositionSlot(int encodedDistance)
    {
        // 0..2 select repeated offsets; other values are distance + 2
        if (encodedDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedDistance));
        }

        if (encodedDistance < 4)
        {
            return (encodedDistance, 0);
        }

        // Once widths reach 17 bits, all remaining slots have that width
        const int fixedWidthBase = 1 << (Constants.MaximumDistanceExtraBits + 1);
        if (encodedDistance >= fixedWidthBase)
        {
            var slot = 2 * (Constants.MaximumDistanceExtraBits + 1) +
                       ((encodedDistance - fixedWidthBase) >> Constants.MaximumDistanceExtraBits);

            if (slot >= Constants.MaximumPositionSlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(encodedDistance));
            }

            return (slot, Constants.MaximumDistanceExtraBits);
        }

        // Its leading bit selects a slot pair; the next bit selects it's half
        var highestBit = (int)Math.Log(encodedDistance, 2);
        var extraBits = highestBit - 1;
        return (2 * highestBit + ((encodedDistance >> extraBits) & 1), extraBits);
    }
}

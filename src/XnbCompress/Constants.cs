namespace XnbCompress;

internal static class Constants
{
    // LZX alphabet sizes and match limits
    public const int MinimumMatchLength = 2;
    public const int MaximumMatchLength = 257;
    public const int LiteralSymbolCount = 256;
    public const int PrimaryLengthCount = 7;
    public const int SecondaryMinimumLength = MinimumMatchLength + PrimaryLengthCount;
    public const int SecondaryLengthCount = MaximumMatchLength - SecondaryMinimumLength + 1;
    public const int LengthCodeBitCount = 3;
    public const int LengthCodesPerSlot = PrimaryLengthCount + 1;
    public const int PretreeSymbolCount = 20;
    public const int AlignedSymbolCount = 8;

    // Supported windows bound the distance alphabet
    public const int MinimumWindowExponent = 15;
    public const int MaximumWindowExponent = 21;
    public const int MaximumDistanceExtraBits = 17;
    public const int MaximumPositionSlotCount = 50;

    // Fields in the block and tree descriptions
    public const int BlockTypeBitCount = 3;
    public const int BlockLengthBitCount = 24;
    public const int MaximumBlockLength = (1 << BlockLengthBitCount) - 1;
    public const int MaximumCodeLength = 16;
    public const int LengthDeltaModulo = MaximumCodeLength + 1;
    public const int PretreeLengthBitCount = 4;
    public const int AlignedLengthBitCount = 3;
    public const int AlignedDistanceBitCount = 3;
    public const int AlignedDistanceMask = (1 << AlignedDistanceBitCount) - 1;

    // Pretree instruction symbols, following length deltas 0..16
    public const int ShortZeroRunSymbol = 17;
    public const int LongZeroRunSymbol = 18;
    public const int RepeatedLengthSymbol = 19;

    // Record boundaries use decoded bytes while headers store encoded sizes
    public const int RecordDecodedCapacity = 0x8000;
    public const byte FinalRecordMarker = 0xFF;
    public const int FinalRecordTrailerLength = 5;
}

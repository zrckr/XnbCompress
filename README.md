# XnbCompressor

XnbCompressor is an implementation of the LZX compression algorithm
used for binary data in the XNA Framework Content Pipeline.

The encoder is a managed C# reimplementation based on analysis of `xcompress.dll`.

The decoder derives from the libmspack LZX decoder via Ali Scissons' C# port.

## Scope

- Targets .NET Standard 2.1.
- Reads and writes the framed LZX payloads used by XNB files.

> [!WARNING]
> It does not parse XNB headers or deserialize assets!

## Usage

```csharp
using System;
using System.IO;
using XnbCompress;

const int windowExponent = 16;
const int partitionSize = 0x80000;

byte[] source = new byte[] { 1, 2, 3, 4, 5 };
using var input = new MemoryStream(source, writable: false);
using var compressed = new MemoryStream();

var encoder = new XMemCompressor(windowExponent, partitionSize);
int compressedLength = encoder.Compress(input, source.Length, compressed, int.MaxValue);

compressed.Position = 0;
using var restored = new MemoryStream();
var decoder = new XMemDecompressor(windowExponent);
int status = decoder.Decompress(compressed, compressedLength, restored, source.Length);

if (status != 0 || restored.Length != source.Length)
{
    throw new InvalidDataException("Decompression failed.");
}

byte[] result = restored.ToArray();
```

> [!NOTE]
> `windowExponent` specifies a window of `1 << windowExponent` bytes and must be between 15 and 21.
> The example uses the test suite's configuration: a 64 KiB window and a 512 KiB partition.

For complete XNB files, see the test helper [XnbLogic.cs](src/XnbCompress.Test/XnbLogic.cs),
which extracts the payload and updates the container headers.
This helper is not part of the public library.

## Build and test

The test project targets .NET 10. Native comparison tests require Windows and
`xcompress32.dll` or `xcompress64.dll`, matching the test process architecture.

Use a .NET 10 SDK to build the solution:

```powershell
dotnet build XnbCompress.slnx -c Release
```

To configure the tests, copy `template.runsettings` to `.runsettings` and set the two sample paths:

```xml

<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <XNB_COMPRESSED_ROOT>C:\Samples\compressed</XNB_COMPRESSED_ROOT>
      <XNB_DECOMPRESSED_ROOT>C:\Samples\decompressed</XNB_DECOMPRESSED_ROOT>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

Run the tests:

```powershell
dotnet test src/XnbCompress.Test/XnbCompress.Test.csproj -c Release --settings .runsettings
```

Both folders must contain corresponding `.xnb` files at the same relative paths.
Unpaired files and already-uncompressed files in the compressed folder are skipped.
Missing counterparts are not reconstructed. Discovery fails if no compressed pairs are found.

## Testing corpus

| Game                                  | Number of compressed XNBs |
|---------------------------------------|---------------------------|
| FEZ                                   | 2201                      |
| Owlboy                                | 6453                      |
| Duck Game[^1]                         | 1663                      |
| OneShot World Machine Edition[^1][^2] | 1756                      |
| Dust: An Elysian Tail[^3]             | 399                       |
| Apotheon                              | 3125                      |
| **TOTAL**                             | **15597**                 |

[^1]: The content is originally non-compressed, but was compressed via native library.
[^2]: Targets MonoGame, but the content distributed via XNBs.
[^3]: It has large assets to test against.

## Licensing

Original project code is licensed under [MIT](licenses/LICENSE).

Decoder-derived code retains its separate [LGPL-2.1 or MS-PL license](licenses/lzxdecoder.LICENSE) and upstream notices.

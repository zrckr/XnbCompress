using System.Runtime.InteropServices;

namespace XnbCompress.Test;

// Bind only public exports, with fresh contexts per call and process-sized size_t values.
internal sealed class XCompress : IDisposable
{
    private readonly IntPtr _module;
    private readonly CreateContext _createCompression;
    private readonly CreateContext _createDecompression;
    private readonly Transform _compress;
    private readonly Transform _decompress;
    private readonly DestroyContext _destroyCompression;
    private readonly DestroyContext _destroyDecompression;

    private enum XnbCompress
    {
        Lzx = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public uint Flags;
        public uint WindowSize;
        public uint PartitionSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateContext(XnbCompress xnbCompress, ref Parameters parameters, uint flags, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Transform(IntPtr context, [Out] byte[] destination, ref nuint destinationSize, byte[] source, nuint sourceSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DestroyContext(IntPtr context);

    public XCompress()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native oracle tests require Windows.");
        }

        var name = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "xcompress64.dll",
            Architecture.X86 => "xcompress32.dll",
            _ => throw new PlatformNotSupportedException("Native oracle requires an x86 or x64 test host.")
        };

        _module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name));
        try
        {
            _createCompression = Bind<CreateContext>("XMemCreateCompressionContext");
            _createDecompression = Bind<CreateContext>("XMemCreateDecompressionContext");
            _compress = Bind<Transform>("XMemCompress");
            _decompress = Bind<Transform>("XMemDecompress");
            _destroyCompression = Bind<DestroyContext>("XMemDestroyCompressionContext");
            _destroyDecompression = Bind<DestroyContext>("XMemDestroyDecompressionContext");
        }
        catch
        {
            NativeLibrary.Free(_module);
            throw;
        }
    }

    public byte[] Compress(byte[] source)
    {
        // Leave headroom for raw records and framing, including tiny inputs.
        var capacity = checked(source.Length * 2 + (1 << XnbLogic.WindowExponent));
        return Run(_createCompression, _compress, _destroyCompression, source, capacity);
    }

    public byte[] Decompress(byte[] source, int decodedLength)
    {
        var decoded = Run(_createDecompression, _decompress, _destroyDecompression, source, decodedLength);
        Assert.Equal(decodedLength, decoded.Length);
        return decoded;
    }

    private static byte[] Run(
        CreateContext create,
        Transform transform,
        DestroyContext destroy,
        byte[] source,
        int capacity)
    {
        var parameters = new Parameters
        {
            WindowSize = 1U << XnbLogic.WindowExponent,
            PartitionSize = XnbLogic.PartitionSize
        };
        var status = create(XnbCompress.Lzx, ref parameters, 0, out var context);
        if (status != 0)
        {
            throw new InvalidOperationException($"XMem context creation failed: 0x{status:X8}.");
        }

        try
        {
            var output = new byte[capacity];
            var length = (nuint)output.Length;
            status = transform(context, output, ref length, source, (nuint)source.Length);
            if (status != 0)
            {
                throw new InvalidOperationException($"XMem transform failed: 0x{status:X8}.");
            }

            Assert.True(length <= (nuint)output.Length, "Native result exceeds destination capacity.");
            Array.Resize(ref output, checked((int)length));
            return output;
        }
        finally
        {
            destroy(context);
        }
    }

    private T Bind<T>(string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));
    }

    public void Dispose()
    {
        NativeLibrary.Free(_module);
    }
}

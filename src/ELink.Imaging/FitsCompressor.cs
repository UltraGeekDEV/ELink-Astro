using System.Runtime.InteropServices;

namespace ELink.Imaging;

/// <summary>Lossless tile compression of FITS images (Rice, as fpack writes them: a <c>.fits.fz</c> that Siril, PixInsight, ASTAP and
/// astropy read as they are), done by the system's cfitsio, the reference implementation. A 16-bit frame of real sky takes about a
/// half to two thirds of the space. Where cfitsio is not installed <see cref="Available"/> is false and frames are kept as they are.</summary>
public static class FitsCompressor
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenMem(out IntPtr fptr, string name, int mode, ref IntPtr buffer, ref UIntPtr size, UIntPtr delta, IntPtr realloc, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(out IntPtr fptr, string name, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetType(IntPtr fptr, int type, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Compress(IntPtr input, IntPtr output, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr fptr, ref int status);

    private const int ReadOnly = 0, Rice1 = 11;
    private static readonly Lazy<(OpenMem Open, Create Create, SetType SetType, Compress Compress, Close Close)?> Lib = new(Load);

    /// <summary>Whether cfitsio was found.</summary>
    public static bool Available => Lib.Value is not null;

    private static (OpenMem, Create, SetType, Compress, Close)? Load()
    {
        foreach (var name in new[] { "libcfitsio.so.10", "libcfitsio.so.9", "libcfitsio.so", "libcfitsio.dylib", "cfitsio" })
        {
            if (!NativeLibrary.TryLoad(name, out var h)) continue;
            try
            {
                T F<T>(string export) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(h, export));
                return (F<OpenMem>("ffomem"), F<Create>("ffinit"), F<SetType>("fits_set_compression_type"), F<Compress>("fits_img_compress"), F<Close>("ffclos"));
            }
            catch (EntryPointNotFoundException) { }
        }
        return null;
    }

    /// <summary>Writes <paramref name="fits"/> (a whole FITS file, first HDU an image) to <paramref name="path"/> Rice-compressed. False,
    /// and nothing written, when cfitsio is not there or cannot compress it (the caller then keeps the plain file).</summary>
    public static bool TryWrite(byte[] fits, string path)
    {
        if (Lib.Value is not { } lib) return false;
        var handle = GCHandle.Alloc(fits, GCHandleType.Pinned);      // read in place: a 24 MP frame is 48 MB, a copy is felt on a Pi
        IntPtr input = IntPtr.Zero, output = IntPtr.Zero;
        int status = 0;
        try
        {
            IntPtr buffer = handle.AddrOfPinnedObject(); UIntPtr size = (UIntPtr)fits.Length;
            lib.Open(out input, "frame.fits", ReadOnly, ref buffer, ref size, UIntPtr.Zero, IntPtr.Zero, ref status);
            if (status != 0) return false;
            if (File.Exists(path)) File.Delete(path);
            lib.Create(out output, path, ref status);
            if (status != 0) return false;
            lib.SetType(output, Rice1, ref status);
            lib.Compress(input, output, ref status);
            return status == 0;
        }
        finally
        {
            int s = 0;
            if (output != IntPtr.Zero) { lib.Close(output, ref s); if (status != 0) try { File.Delete(path); } catch (IOException) { } }
            s = 0;
            if (input != IntPtr.Zero) lib.Close(input, ref s);
            handle.Free();
        }
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JsonFormatter.Media;

/// <summary>
/// 媒体解码分派：原生格式走 WPF，webp/svg 走 SkiaSharp 插件（反射），
/// HEIC 走 Windows Shell 缩略图，PDF 走 pdfium P/Invoke。全部可优雅退化。
/// </summary>
public static class MediaCodecs
{
    public sealed record DecodedImage(BitmapSource Source, int Width, int Height);

    public static bool SkiaReady => FindType("SkiaSharp", "SkiaSharp.SKBitmap") is not null;
    /// <summary>最近一次解码失败的原因（卡片上展示，便于定位）。</summary>
    public static string? LastDecodeError { get; private set; }

    // ---------- 图片 ----------

    public static DecodedImage? DecodeImage(byte[] bytes, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            LastDecodeError = null;
            return ext switch
            {
                ".webp" => DecodeWithSkia(bytes),
                ".svg" => RenderSvg(bytes),
                ".heic" or ".heif" => DecodeHeicShell(bytes),
                _ => DecodeNative(bytes), // jpg/png/bmp/gif 等
            };
        }
        catch (Exception ex)
        {
            LastDecodeError = ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } ie ? " | " + ie.Message : "");
            return null;
        }
    }

    private static DecodedImage? DecodeNative(byte[] bytes)
    {
        var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return new DecodedImage(frame, frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>HEIC：落盘临时文件后取 Windows Shell 缩略图（需系统 HEIF 扩展）。</summary>
    private static DecodedImage? DecodeHeicShell(byte[] bytes)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "jf-" + Guid.NewGuid().ToString("N") + ".heic");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            using var thumb = ShellThumbnail.GetThumbnail(tmp, 1024);
            if (thumb is null) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(thumb.Handle, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return new DecodedImage(src, src.PixelWidth, src.PixelHeight);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>从已加载程序集中找类型（插件经 Assembly.LoadFrom 加载，Type.GetType 探测不到）。</summary>
    private static Type? FindType(string assemblyName, string fullTypeName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            ?.GetType(fullTypeName, throwOnError: false);

    // ---------- Skia（webp 反射 / svg 反射）----------

    private static DecodedImage? DecodeWithSkia(byte[] bytes)
    {
        var skBitmapType = FindType("SkiaSharp", "SkiaSharp.SKBitmap") ?? throw new InvalidOperationException("SkiaSharp 未加载");
        // 2.88 有 SKBitmap(byte[]) 构造；4.x 改为静态 Decode(byte[])
        object? bm = skBitmapType.GetConstructor(new[] { typeof(byte[]) }) is { } ctor
            ? ctor.Invoke(new object[] { bytes })
            : skBitmapType.GetMethod("Decode", new[] { typeof(byte[]) })?.Invoke(null, new object[] { bytes });
        if (bm is null) return null;
        return SkiaToImage(bm, skBitmapType);
    }

    private static DecodedImage? RenderSvg(byte[] bytes)
    {
        var svgType = FindType("Svg.Skia", "Svg.Skia.SKSvg") ?? throw new InvalidOperationException("Svg.Skia 未加载");
        var svg = Activator.CreateInstance(svgType);
        if (svg is null) return null;
        // SKSvg.Load(Stream)
        var load = svgType.GetMethod("Load", new[] { typeof(Stream) });
        if (load is null) return null;
        using var ms = new MemoryStream(bytes);
        load.Invoke(svg, new object[] { ms });

        var picture = svgType.GetProperty("Picture")?.GetValue(svg);
        if (picture is null) return null;
        var rect = picture.GetType().GetProperty("CullRect")?.GetValue(picture);
        double w = Convert.ToDouble(rect?.GetType().GetProperty("Width")?.GetValue(rect) ?? 512);
        double h = Convert.ToDouble(rect?.GetType().GetProperty("Height")?.GetValue(rect) ?? 512);
        if (w <= 0 || h <= 0) { w = 512; h = 512; }
        int iw = Math.Min((int)Math.Ceiling(w), 2048), ih = Math.Min((int)Math.Ceiling(h), 2048);

        // SKSurface.Create(info) → canvas.DrawPicture → snapshot → PNG
        var infoType = FindType("SkiaSharp", "SkiaSharp.SKImageInfo")!;
        var surfaceType = FindType("SkiaSharp", "SkiaSharp.SKSurface")!;
        var createInfo = Activator.CreateInstance(infoType, new object[] { iw, ih });
        var surface = surfaceType.GetMethod("Create", new[] { infoType })?.Invoke(null, new[] { createInfo });
        if (surface is null) return null;
        var canvas = surface.GetType().GetProperty("Canvas")?.GetValue(surface);
        var paintType = FindType("SkiaSharp", "SkiaSharp.SKPaint")!;
        var colorType = FindType("SkiaSharp", "SkiaSharp.SKColor")!;
        // 4.x 有 Clear() 与 Clear(SKColor) 两个重载，必须带类型取参否则 AmbiguousMatchException
        canvas?.GetType().GetMethod("Clear", new[] { colorType })?
            .Invoke(canvas, new object[] { Activator.CreateInstance(colorType, new object[] { (byte)255, (byte)255, (byte)255, (byte)255 })! });
        canvas?.GetType().GetMethod("DrawPicture", new[] { picture.GetType(), paintType })?
            .Invoke(canvas, new[] { picture, null });
        var image = surface.GetType().GetMethod("Snapshot", Type.EmptyTypes)?.Invoke(surface, null);
        var data = EncodePng(image);
        if (data is null) return null;
        using var stream = (Stream)data.GetType().GetMethod("AsStream", Type.EmptyTypes)!.Invoke(data, null)!;
        using var outMs = new MemoryStream();
        stream.CopyTo(outMs);
        return DecodeNative(outMs.ToArray());
    }

    private static object? EncodePng(object? skImage)
    {
        if (skImage is null) return null;
        // SKImage.Encode(SKEncodedImageFormat.Png, int) → SKData（2.88 API）
        var formatType = FindType("SkiaSharp", "SkiaSharp.SKEncodedImageFormat")!;
        var png = Enum.Parse(formatType, "Png");
        return skImage.GetType().GetMethod("Encode", new[] { formatType, typeof(int) })?
            .Invoke(skImage, new[] { png, 100 });
    }

    private static DecodedImage? SkiaToImage(object skBitmap, Type type)
    {
        int w = (int)(type.GetProperty("Width")?.GetValue(skBitmap) ?? 0);
        int h = (int)(type.GetProperty("Height")?.GetValue(skBitmap) ?? 0);
        if (w <= 0 || h <= 0) return null;

        var imageType = FindType("SkiaSharp", "SkiaSharp.SKImage")!;
        var fromBitmap = imageType.GetMethod("FromBitmap", new[] { type })?.Invoke(null, new[] { skBitmap });
        var data = EncodePng(fromBitmap);
        if (data is null) return null;
        using var stream = (Stream)data.GetType().GetMethod("AsStream", Type.EmptyTypes)!.Invoke(data, null)!;
        using var outMs = new MemoryStream();
        stream.CopyTo(outMs);
        var img = DecodeNative(outMs.ToArray());
        return img;
    }

    // ---------- PDF（pdfium P/Invoke）----------

    public static bool PdfiumReady { get; private set; }

    public sealed record PdfPreview(BitmapSource FirstPage, int PageCount, double WidthPt, double HeightPt);

    public static PdfPreview? RenderPdf(byte[] bytes)
    {
        // FPDF_LoadMemDocument 不拷贝缓冲区：文档存活期间 byte[] 必须钉住，否则 GC 移动后原生指针悬空（AV）
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var doc = Pdfium.FPDF_LoadMemDocument(bytes, bytes.Length, null);
            if (doc == IntPtr.Zero) return null;
            try
            {
                int pages = Pdfium.FPDF_GetPageCount(doc);
                var page = Pdfium.FPDF_LoadPage(doc, 0);
                if (page == IntPtr.Zero) return null;
                try
                {
                    double wPt = Pdfium.FPDF_GetPageWidth(page);
                    double hPt = Pdfium.FPDF_GetPageHeight(page);
                    const double dpi = 150.0 / 72.0;
                    int w = Math.Max(1, (int)(wPt * dpi)), h = Math.Max(1, (int)(hPt * dpi));
                    var bmp = Pdfium.FPDFBitmap_Create(w, h, 1);
                    if (bmp == IntPtr.Zero) return null;
                    try
                    {
                        Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFFu);
                        Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, 0);

                        var stride = Pdfium.FPDFBitmap_GetStride(bmp);
                        var buf = Pdfium.FPDFBitmap_GetBuffer(bmp);
                        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride * h, stride);
                        source.Freeze();
                        return new PdfPreview(source, pages, wPt, hPt);
                    }
                    finally { Pdfium.FPDFBitmap_Destroy(bmp); }
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
            finally { Pdfium.FPDF_CloseDocument(doc); }
        }
        catch
        {
            return null;
        }
        finally
        {
            pin.Free();
        }
    }

    internal static class Pdfium
    {
        private const string Dll = "pdfium.dll";
        private static bool _tried;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDF_LoadMemDocument(byte[] data, int size, string? password);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double FPDF_GetPageWidth(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double FPDF_GetPageHeight(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int l, int t, int w, int h, uint argb);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_InitLibrary();

        public static bool TryLoad()
        {
            if (_tried) return PdfiumReady;
            _tried = true;
            try
            {
                // 触发一次真实调用验证 dll 在位
                PdfiumReady = NativeLibrary.TryLoad("pdfium", out _)
                              || NativeLibrary.TryLoad(
                                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "JsonFormatter", "plugins", "native", "pdfium.dll"), out _);
                if (PdfiumReady)
                    FPDF_InitLibrary(); // 新版 pdfium 必须先初始化，否则 FPDF_LoadPage 原生 AV
                return PdfiumReady;
            }
            catch { return PdfiumReady = false; }
        }
    }
}

/// <summary>Windows Shell 缩略图（HEIC 等系统有 codec 的格式）。</summary>
internal static class ShellThumbnail
{
    public sealed class HBitmap : IDisposable
    {
        public IntPtr Handle;
        public HBitmap(IntPtr h) => Handle = h;
        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DeleteObject(Handle);
                Handle = IntPtr.Zero;
            }
        }
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    }

    public static HBitmap? GetThumbnail(string path, int size)
    {
        try
        {
            var guid = new Guid("bcc18b79-ba16-442f-8c4f-2e1c3e2afb45"); // IShellItemImageFactory
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, guid, out var factory);
            if (hr != 0 || factory == IntPtr.Zero) return null;
            try
            {
                int hr2 = GetImage(factory, size, size, 0x4 | 0x20 /*RESIZETOFIT|BIGGERSIZEOK*/, out var hbm); // vtable slot 3
                if (hr2 != 0 || hbm == IntPtr.Zero) return null;
                return new HBitmap(hbm);
            }
            finally { Marshal.Release(factory); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, in Guid riid, out IntPtr ppv);

    private static int GetImage(IntPtr factory, int cx, int cy, int flags, out IntPtr hbm)
    {
        // IShellItemImageFactory::GetImage(SIZE, SIIGBF, HBITMAP*) —— vtable 第 4 个方法（slot 3）
        var vtbl = Marshal.ReadIntPtr(factory);
        var fn = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        var d = (GetImageDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetImageDelegate));
        return d(factory, cx, cy, flags, out hbm);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetImageDelegate(IntPtr self, int cx, int cy, int flags, out IntPtr hbm);
}

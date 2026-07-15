// First-page PDF → Bitmap via bundled PDFium (thirdparty\pdfium.dll.api → <LB>\ThirdParty\Pdfium\pdfium.dll).
//
// Why PDFium and not ImageMagick/WebView2: Magick can't rasterize a PDF without Ghostscript, and WebView2's
// PDF viewer (net10-only) captures the viewer chrome and needs a UI thread + finicky render timing. PDFium
// renders a page to a raw BGRA buffer synchronously, off the UI thread, at any size, with no chrome — the
// right tool for a document thumbnail. Licence: BSD-3 (Google).
//
// pdfium's core is NOT thread-safe, so every call is serialised under one lock. The library is loaded by FULL
// PATH (NativeLibrary.Load) so it needn't sit on the DLL search path; subsequent [DllImport("pdfium")] calls
// resolve the already-loaded module by name. Everything degrades to null when the DLL is absent.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Media;

internal static class PdfThumbnailer
{
    private const string DLL = "pdfium";

    [DllImport(DLL)] private static extern void FPDF_InitLibrary();
    [DllImport(DLL, CharSet = CharSet.Ansi)] private static extern IntPtr FPDF_LoadDocument(string path, string? password);
    [DllImport(DLL)] private static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(DLL)] private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(DLL)] private static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(DLL)] private static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(DLL)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(DLL)] private static extern void FPDFBitmap_FillRect(IntPtr bmp, int left, int top, int width, int height, uint color);
    [DllImport(DLL)] private static extern void FPDF_RenderPageBitmap(IntPtr bmp, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(DLL)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bmp);
    [DllImport(DLL)] private static extern int FPDFBitmap_GetStride(IntPtr bmp);
    [DllImport(DLL)] private static extern void FPDFBitmap_Destroy(IntPtr bmp);
    [DllImport(DLL)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(DLL)] private static extern void FPDF_CloseDocument(IntPtr doc);

    private const int FPDF_ANNOT = 0x01;   // render annotations / form fields too

    private static readonly object _lock = new();
    private static string _dllPath = "";
    private static bool _init, _tried;

    /// <summary>Point the loader at the deployed pdfium.dll (call once at boot with the LB root).</summary>
    public static void Configure(string? lbRoot) => _dllPath = Install.NativeInstaller.PdfiumPath(lbRoot);

    /// <summary>True when pdfium is present and initialised (loads it on first ask).</summary>
    public static bool Available { get { lock (_lock) return EnsureLoaded(); } }

    private static bool EnsureLoaded()
    {
        if (_init) return true;
        if (_tried) return false;
        _tried = true;
        try
        {
            if (string.IsNullOrEmpty(_dllPath) || !File.Exists(_dllPath)) return false;
            NativeLibrary.Load(_dllPath);   // full path → the [DllImport("pdfium")] calls resolve this module
            FPDF_InitLibrary();
            _init = true;
        }
        catch { _init = false; }
        return _init;
    }

    /// <summary>Render page 0 of <paramref name="pdfPath"/> to a Bitmap whose longest edge is ≈ <paramref name="maxDim"/>
    /// px (white background, aspect preserved). Null on any failure. Serialised; safe off the UI thread.</summary>
    public static Bitmap? RenderFirstPage(string pdfPath, int maxDim)
    {
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath) || maxDim < 1) return null;
        lock (_lock)
        {
            if (!EnsureLoaded()) return null;
            IntPtr doc = IntPtr.Zero, page = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                doc = FPDF_LoadDocument(pdfPath, null);
                if (doc == IntPtr.Zero || FPDF_GetPageCount(doc) < 1) return null;
                page = FPDF_LoadPage(doc, 0);
                if (page == IntPtr.Zero) return null;

                double pw = FPDF_GetPageWidth(page), ph = FPDF_GetPageHeight(page);
                if (pw <= 0 || ph <= 0) return null;
                double scale = Math.Min(maxDim / pw, maxDim / ph);
                if (scale <= 0) scale = 1;
                int w = Math.Max(1, (int)Math.Round(pw * scale)), h = Math.Max(1, (int)Math.Round(ph * scale));

                bmp = FPDFBitmap_Create(w, h, 0);
                if (bmp == IntPtr.Zero) return null;
                FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);   // opaque white page
                FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, FPDF_ANNOT);

                IntPtr buf = FPDFBitmap_GetBuffer(bmp);
                int stride = FPDFBitmap_GetStride(bmp);
                if (buf == IntPtr.Zero || stride <= 0) return null;

                // pdfium's buffer is BGRA, which is exactly Format32bppArgb's byte order on little-endian.
                using var wrap = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, buf);
                return new Bitmap(wrap);   // deep copy so the pdfium buffer can be freed in finally
            }
            catch { return null; }
            finally
            {
                if (bmp != IntPtr.Zero) FPDFBitmap_Destroy(bmp);
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            }
        }
    }
}

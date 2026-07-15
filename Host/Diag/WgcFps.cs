// SmartCapture core: measure the REAL presentation rate (FPS) of a specific window via
// Windows Graphics Capture (WGC) — the same non-invasive, non-admin capture Magpie/OBS use.
// A window that presents frames continuously (30/60 fps) is an actively-rendering game; a
// static splash / config window presents ~0 fps. This is the only robust "the game is
// rendering" signal — API-agnostic (D3D9/11/12, Vulkan, OpenGL, even software present through
// DWM composition) and size-agnostic (works for a small windowed emulator too), unlike GPU
// counters (miss CPU-bound games) or window-size heuristics (miss deliberate windowed mode).
//
// We only COUNT FrameArrived events — the pixels are dequeued and discarded immediately (the
// pool must be drained or it stalls). Cheap. Requires Windows 10 1803+ (present in Core).

#nullable enable

using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LbApiHost.Host.Diag;

internal sealed class WgcFps : IDisposable
{
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDirect3DDevice _device;
    private int _frames;

    private WgcFps(GraphicsCaptureItem item, IDirect3DDevice device, bool showBorder)
    {
        _device = device;
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) size = new Windows.Graphics.SizeInt32 { Width = 8, Height = 8 };
        // FreeThreaded: FrameArrived fires on a threadpool thread — our probe runs on a plain
        // background thread with NO DispatcherQueue, which the non-free-threaded Create requires.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _pool.FrameArrived += (s, _) =>
        {
            try { using var f = s.TryGetNextFrame(); System.Threading.Interlocked.Increment(ref _frames); }
            catch { }
        };
        _session = _pool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        // The yellow WGC capture border. Off by default (SmartCapture only measures fps — the pixels
        // are discarded — so the border is pure noise). On Win11 clearing it needs the "borderless"
        // capture grant first (else the set throws and the border stays) — done in TryCreate. The
        // hidden LiteBox.ini key SmartCaptureShowBorder=true forces it back on.
        try { _session.IsBorderRequired = showBorder; }
        catch (Exception ex) { Console.WriteLine($"[wgc] IsBorderRequired={showBorder} failed: {ex.GetType().Name}: {ex.Message}"); }
        _session.StartCapture();
    }

    // Windows 11 requires an explicit "borderless" grant before IsBorderRequired=false is honoured.
    // Requested once per process; harmless / no-op on OSes without the API (older Win10 draws no border).
    private static int _borderlessTried;
    private static void EnsureBorderlessAccess()
    {
        if (System.Threading.Interlocked.Exchange(ref _borderlessTried, 1) != 0) return;
        try
        {
            var r = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).GetAwaiter().GetResult();
            Console.WriteLine($"[wgc] borderless access: {r}");
        }
        catch (Exception ex) { Console.WriteLine($"[wgc] borderless access request failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Frames counted since the last call, reset to 0. Divide by the elapsed seconds for FPS.</summary>
    public int TakeFrames() => System.Threading.Interlocked.Exchange(ref _frames, 0);

    /// <summary>Start measuring the given window, or null if WGC is unavailable / the HWND can't be captured.
    /// <paramref name="showBorder"/> keeps the yellow WGC capture border (hidden LiteBox.ini opt-in).
    /// <paramref name="sharedDevice"/>: a device from <see cref="CreateSharedDevice"/> to reuse across every
    /// meter in one SmartCapture run, instead of each meter creating (and never releasing until RCW-GC) its own
    /// D3D11 device — a store launch with several new top-level windows to consider can otherwise stack up
    /// several live devices at once for no benefit, since one device can back many capture sessions. Omit to
    /// create a private device for this instance alone.</summary>
    public static WgcFps? TryCreate(IntPtr hwnd, bool showBorder = false, IDirect3DDevice? sharedDevice = null)
    {
        string step = "supported";
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { Console.WriteLine("[wgc] not supported"); return null; }
            if (!showBorder) EnsureBorderlessAccess();
            step = "device"; var device = sharedDevice ?? CreateDevice();
            step = "item"; var item = CreateItemForWindow(hwnd);
            if (item == null) { Console.WriteLine("[wgc] item null"); return null; }
            step = "session"; return new WgcFps(item, device, showBorder);
        }
        catch (Exception ex) { Console.WriteLine($"[wgc] TryCreate failed at '{step}': {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    /// <summary>One D3D11 device a caller shares across every <see cref="TryCreate"/> call in a single
    /// SmartCapture run (passed as its <c>sharedDevice</c>). The caller owns it — dispose it once at the end,
    /// after every meter built from it is disposed.</summary>
    public static IDirect3DDevice CreateSharedDevice() => CreateDevice();

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
    }

    // ── D3D11 device + WinRT IDirect3DDevice wrapper ──────────────────────────
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static IDirect3DDevice CreateDevice()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out var d3d, out _, out var ctx);
        if (hr != 0 || d3d == IntPtr.Zero) throw new InvalidOperationException($"D3D11CreateDevice hr=0x{hr:X}");
        try
        {
            Guid iid = IID_IDXGIDevice;
            Marshal.QueryInterface(d3d, ref iid, out var dxgi);
            try
            {
                int hr2 = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var abi);
                if (hr2 != 0 || abi == IntPtr.Zero) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr2:X}");
                // IDirect3DDevice is a projected INTERFACE → MarshalInterface (MarshalInspectable is for classes).
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(abi); }
                finally { Marshal.Release(abi); }
            }
            finally { if (dxgi != IntPtr.Zero) Marshal.Release(dxgi); }
        }
        finally
        {
            if (ctx != IntPtr.Zero) Marshal.Release(ctx);
            Marshal.Release(d3d);
        }
    }

    // ── GraphicsCaptureItem from an HWND (IGraphicsCaptureItemInterop — a plain IUnknown COM
    //    interface, NOT in the WinRT projection). Called via a manual vtable invocation: the RCW
    //    cast (GetObjectForIUnknown → interface) doesn't work for it under modern .NET. ─────────
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowFn(IntPtr thisPtr, IntPtr hwnd, ref Guid iid, out IntPtr result);

    private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Guid interopIid = IID_IGraphicsCaptureItemInterop;
        Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out var interop);
        if (interop == IntPtr.Zero) return null;
        try
        {
            // vtable: [0..2]=IUnknown, [3]=CreateForWindow(HWND, REFIID, void** result).
            IntPtr vtbl = Marshal.ReadIntPtr(interop);
            var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowFn>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
            Guid iid = IID_IGraphicsCaptureItem;
            int hr = createForWindow(interop, hwnd, ref iid, out IntPtr ptr);
            if (hr != 0 || ptr == IntPtr.Zero) { Console.WriteLine($"[wgc] CreateForWindow hr=0x{hr:X}"); return null; }
            try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr); }
            finally { Marshal.Release(ptr); }
        }
        finally { Marshal.Release(interop); }
    }
}

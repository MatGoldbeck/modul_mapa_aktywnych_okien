using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace SpikeTest;

internal sealed record CaptureResult(string Method, bool Ok, long Ms, string Note, string? File);

/// <summary>
/// Dwa sposoby zrobienia bitmapy konkretnego okna:
///  A) PrintWindow(PW_RENDERFULLCONTENT) – GDI, działa od Windows 8.1,
///  B) Windows.Graphics.Capture (WGC) – ten sam mechanizm co narzędzia do nagrywania ekranu.
/// Wynik zapisujemy jako PNG o szerokości 320 px (WebP w prototypie właściwym).
/// </summary>
internal static class Thumbnails
{
    public const int ThumbWidth = 320;

    // --- A) PrintWindow ---------------------------------------------------

    public static CaptureResult CapturePrintWindow(WindowInfo w, string outPath)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!Native.GetWindowRect(w.Hwnd, out var r) || r.Width <= 0 || r.Height <= 0)
                return new("PrintWindow", false, sw.ElapsedMilliseconds, "brak rozmiaru okna", null);

            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { ok = Native.PrintWindow(w.Hwnd, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!ok)
                return new("PrintWindow", false, sw.ElapsedMilliseconds, "PrintWindow zwrócił false", null);

            var note = SaveThumb(bmp, outPath);
            return new("PrintWindow", true, sw.ElapsedMilliseconds, note, outPath);
        }
        catch (Exception ex)
        {
            return new("PrintWindow", false, sw.ElapsedMilliseconds, ex.Message, null);
        }
    }

    // --- B) Windows.Graphics.Capture --------------------------------------

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice? _device;
    private static bool _accessRequested;

    public static string WgcStatus()
    {
        var parts = new List<string>
        {
            "WGC obsługiwane: " + (GraphicsCaptureSession.IsSupported() ? "tak" : "NIE"),
            "IsBorderRequired dostępne: " + (BorderPropertyPresent ? "tak" : "nie (żółta ramka podczas przechwytywania)"),
        };
        return string.Join(", ", parts);
    }

    private static bool BorderPropertyPresent =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    private static async Task EnsureAccessAsync()
    {
        if (_accessRequested) return;
        _accessRequested = true;
        if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess")) return;
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Log.Line($"  [WGC] zgoda na przechwytywanie bez ramki: {status}");
        }
        catch (Exception ex)
        {
            Log.Line($"  [WGC] RequestAccessAsync: {ex.Message}");
        }
    }

    private static IDirect3DDevice GetDevice()
    {
        if (_device != null) return _device;

        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;

        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out var d3dDevice, out _, out var context));
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    _device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
        return _device;
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public static async Task<CaptureResult> CaptureWgcAsync(WindowInfo w, string outPath, int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return new("WGC", false, 0, "WGC nieobsługiwane w tym systemie", null);

            await EnsureAccessAsync();
            var item = CreateItem(w.Hwnd);
            var device = GetDevice();

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);

            var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (p, _) =>
            {
                var f = p.TryGetNextFrame();
                if (f != null && !tcs.TrySetResult(f)) f.Dispose();
            };

            session.IsCursorCaptureEnabled = false;
            if (BorderPropertyPresent)
            {
                try { session.IsBorderRequired = false; }
                catch (Exception ex) { Log.Line($"  [WGC] IsBorderRequired=false odrzucone: {ex.Message}"); }
            }
            session.StartCapture();

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (finished != tcs.Task)
                return new("WGC", false, sw.ElapsedMilliseconds, $"brak klatki w {timeoutMs} ms", null);

            using var frame = tcs.Task.Result;
            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();
            stream.Seek(0);

            using var bmp = new Bitmap(stream.AsStream());
            var note = SaveThumb(bmp, outPath);
            return new("WGC", true, sw.ElapsedMilliseconds, note, outPath);
        }
        catch (Exception ex)
        {
            return new("WGC", false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    // --- Wspólne ----------------------------------------------------------

    /// <summary>Skaluje do 320 px szerokości, zapisuje PNG i zwraca ocenę zawartości.</summary>
    private static string SaveThumb(Bitmap source, string outPath)
    {
        int h = Math.Max(1, (int)Math.Round(source.Height * (ThumbWidth / (double)source.Width)));
        using var thumb = new Bitmap(ThumbWidth, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, ThumbWidth, h);
        }
        thumb.Save(outPath, ImageFormat.Png);
        return $"{source.Width}x{source.Height}, {DescribeContent(thumb)}";
    }

    /// <summary>Wykrywa obrazy „puste” (czarne / jednolite), typowe dla nieudanego przechwycenia.</summary>
    private static string DescribeContent(Bitmap bmp)
    {
        int samples = 0, black = 0;
        var colors = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += 4)
        for (int x = 0; x < bmp.Width; x += 4)
        {
            var c = bmp.GetPixel(x, y);
            samples++;
            if (c.R < 8 && c.G < 8 && c.B < 8) black++;
            if (colors.Count < 64) colors.Add(c.ToArgb() & 0xF0F0F0);
        }
        if (samples == 0) return "pusty";
        if (black > samples * 0.98) return "CZARNY obraz";
        if (colors.Count <= 2) return "JEDNOLITY obraz";
        return "obraz z treścią";
    }
}

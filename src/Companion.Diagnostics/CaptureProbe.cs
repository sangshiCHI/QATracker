using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinRT;

namespace Companion.Diagnostics;

internal record OcrWordEvidence(string Text, double X, double Y, double Width, double Height);
internal record OcrLineEvidence(string Text, OcrWordEvidence[] Words);
internal record CaptureResult(string Status, double CaptureMs, double? OcrMs, Box Frame, Region Roi,
    string Language, int LuminanceRange, bool PossibleBlank, string Text, OcrLineEvidence[] Lines, string? OcrError)
{
    public double? Confidence => null;
    public double? RuleConfirmationScore => null;
    public bool IsConfirmed => false;
    public string Notice => "诊断输出不是掉落事件；Windows OCR 不提供原生置信度，未执行品质分类或自动计数。";
}

internal static class CaptureProbe
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint hwnd, in Guid iid, out nint result);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }
    [DllImport("combase.dll", CharSet=CharSet.Unicode)] static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint levels, uint levelCount, uint sdkVersion, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);

    internal static string[] Languages() => OcrEngine.AvailableRecognizerLanguages.Select(l=>l.LanguageTag).ToArray();
    internal static bool Supported() => GraphicsCaptureSession.IsSupported();

    static GraphicsCaptureItem Item(nint hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint str=0, factory=0, item=0;
        object? interop=null;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out str));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(str, typeof(ICaptureItemInterop).GUID, out factory));
            interop=Marshal.GetObjectForIUnknown(factory);
            Marshal.ThrowExceptionForHR(((ICaptureItemInterop)interop).CreateForWindow(hwnd,
                new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"), out item));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if(item!=0) Marshal.Release(item);
            if(interop is not null) Marshal.ReleaseComObject(interop);
            if(factory!=0) Marshal.Release(factory);
            if(str!=0) WindowsDeleteString(str);
        }
    }

    static IDirect3DDevice Device()
    {
        nint native=0, context=0, dxgi=0, inspectable=0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context));
            var iid=new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            foreach(var ptr in new[]{inspectable,dxgi,context,native}) if(ptr!=0) Marshal.Release(ptr);
        }
    }

    public static async Task<CaptureResult> Run(nint hwnd, Region roi, string output, string language, CancellationToken token)
    {
        if (!Supported()) throw new NotSupportedException("Windows Graphics Capture 不可用。");
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) throw new InvalidOperationException("窗口已失效或最小化；请恢复游戏后重试。");
        var timer=Stopwatch.StartNew();
        var item=Item(hwnd);
        roi.Validate(item.Size.Width, item.Size.Height);
        using var device=Device();
        using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session=pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled=false;
        session.StartCapture(); // Keep Windows' capture border. Never request borderless access.
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        Direct3D11CaptureFrame? frame=null;
        try
        {
            while(frame is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                frame=pool.TryGetNextFrame();
                if(frame is null) await Task.Delay(80, timeout.Token);
            }
            // Whole window exists transiently in GPU/CPU memory; only the explicit ROI is persisted.
            using var bitmap=await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(timeout.Token);
            roi.Validate(frame.ContentSize.Width, frame.ContentSize.Height);
            using var converted=SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bytes=new byte[checked(converted.PixelWidth*converted.PixelHeight*4)];
            using(var buffer=new DataWriter())
            {
                buffer.WriteBytes(bytes);
                var pixels=buffer.DetachBuffer(); converted.CopyToBuffer(pixels);
                using var reader=DataReader.FromBuffer(pixels); reader.ReadBytes(bytes);
            }
            var source=BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, bytes, converted.PixelWidth*4);
            var crop=new CroppedBitmap(source, new System.Windows.Int32Rect(roi.X,roi.Y,roi.Width,roi.Height));
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
            using var png=new MemoryStream(); encoder.Save(png);
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Combine(output,"roi.png"),png.ToArray(),token);
            await File.WriteAllTextAsync(Path.Combine(output,"roi.png.sha256"),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png.ToArray())),token);
            var captureMs=timer.Elapsed.TotalMilliseconds;
            var raw=new byte[checked(roi.Width*roi.Height*4)]; crop.CopyPixels(raw,roi.Width*4,0);
            int low=255,high=0;
            for(var i=0;i<raw.Length;i+=4) { var light=(raw[i]+raw[i+1]+raw[i+2])/3; low=Math.Min(low,light); high=Math.Max(high,light); }
            var dimensions=new Box(0,0,frame.ContentSize.Width,frame.ContentSize.Height);
            if(high-low<5) return new("BlankOrUniformRegion",captureMs,null,dimensions,roi,language,high-low,true,"",[],
                "区域近乎纯色，不能作为有效游戏信息。不会识别或累计；请核对目标窗口和区域。");
            png.Position=0;
            using var stream=png.AsRandomAccessStream();
            var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(timeout.Token);
            using var input=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore).AsTask(timeout.Token);
            var engine=OcrEngine.TryCreateFromLanguage(new Language(language));
            if(engine is null) return new("CaptureOnly",captureMs,null,dimensions,roi,language,high-low,false,"",[], $"未安装 {language} OCR 语言");
            var ocrTimer=Stopwatch.StartNew();
            var result=await engine.RecognizeAsync(input).AsTask(timeout.Token);
            return new("UnconfirmedOcr",captureMs,ocrTimer.Elapsed.TotalMilliseconds,dimensions,roi,language,high-low,false,
                result.Text,result.Lines.Select(l=>new OcrLineEvidence(l.Text,l.Words.Select(w=>new OcrWordEvidence(w.Text,
                    w.BoundingRect.X,w.BoundingRect.Y,w.BoundingRect.Width,w.BoundingRect.Height)).ToArray())).ToArray(),null);
        }
        finally { frame?.Dispose(); }
    }
}

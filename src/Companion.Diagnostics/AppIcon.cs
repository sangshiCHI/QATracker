using System.Reflection;
using System.Windows.Media.Imaging;

namespace Companion.Diagnostics;

internal static class AppIcon
{
    static readonly Lazy<BitmapFrame> image=new(()=>
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("afk.face-knight.png")!;
        var frame=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);frame.Freeze();return frame;
    });
    internal static BitmapFrame Image=>image.Value;
    internal static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("qatk.ico")!;
        using var icon=new System.Drawing.Icon(stream);return (System.Drawing.Icon)icon.Clone();
    }
}

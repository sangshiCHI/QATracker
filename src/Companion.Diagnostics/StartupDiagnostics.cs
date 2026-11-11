using System.Runtime.InteropServices;
using System.Text.Json;

namespace Companion.Diagnostics;

internal static class StartupDiagnostics
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int MessageBoxW(nint owner,string text,string caption,uint flags);
    internal static bool IsPlayer(string[] args)=>!args.Contains("--synthetic")&&
        (args.Length==0||args.Any(a=>a is "--wiki" or "--afk" or "--update-handoff"));
    internal static string FailureText(Exception error)=>error switch
    {
        UnauthorizedAccessException=>"无法访问助手的数据目录。统计记录没有被重置，请将程序放在可访问的本地目录后重试。",
        DllNotFoundException or BadImageFormatException or FileLoadException=>"助手组件未能加载。请重新完整解压便携包后重试，不需要另外安装 .NET。原来的统计记录保留。",
        IOException=>"助手未能读写本地文件。请检查目录是否可用及磁盘空间，原来的统计记录不会被自动清空。",
        _=>"助手未能正常启动。请稍后重试；已尽可能在本机记录错误代码，不会自动清空统计记录。"
    };
    internal static void Record(string directory,Exception error)
    {
        try
        {
            Directory.CreateDirectory(directory);var file=Path.Combine(directory,"startup-error.json");
            // No raw exception message, stack, account, full path or save content.
            File.WriteAllText(file,JsonSerializer.Serialize(new{AtUtc=DateTimeOffset.UtcNow,Version=typeof(StartupDiagnostics).Assembly.GetName().Version?.ToString(),
                OsBuild=Environment.OSVersion.Version.ToString(),Architecture=RuntimeInformation.ProcessArchitecture.ToString(),Type=error.GetType().Name,HResult=$"0x{error.HResult:X8}"}));
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){ }
    }
    internal static void Notify(string text)
    {
        try{MessageBoxW(0,text,"QATK · 启动提示",0x10);}catch(Exception e)when(e is DllNotFoundException or EntryPointNotFoundException){ }
    }
    internal static void Report(Exception error)
    {
        Record(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuestAwayCompanion"),error);
        Notify(FailureText(error));
    }
}

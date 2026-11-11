using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Companion.Diagnostics;

internal static class GameDiscovery
{
    public static string? FindExe(string configured)
    {
        if(!string.IsNullOrWhiteSpace(configured))return Path.IsPathFullyQualified(configured)&&File.Exists(configured)?configured:null;
        var running=Native.Processes("QuestAway.exe").Where(p=>p.Exe is not null).Select(p=>p.Exe!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(running.Length==1)return running[0];
        using var registry=Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam");
        var root=registry?.GetValue("SteamPath") as string;
        var libraries=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(root is not null)
        {
            libraries.Add(root);var file=Path.Combine(root,"steamapps","libraryfolders.vdf");
            if(File.Exists(file)&&new FileInfo(file).Length<1024*1024)
                foreach(Match m in Regex.Matches(File.ReadAllText(file),"\"path\"\\s*\"([^\"]+)\""))libraries.Add(m.Groups[1].Value.Replace("\\\\","\\"));
        }
        foreach(var library in libraries)
        {
            var manifest=Path.Combine(library,"steamapps","appmanifest_5195600.acf");
            if(!File.Exists(manifest)||new FileInfo(manifest).Length>1024*1024)continue;
            var match=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s*\"([^\"]+)\"");
            var name=match.Groups[1].Value;
            if(string.IsNullOrWhiteSpace(name)||name!=Path.GetFileName(name)||name.Contains(".."))continue;
            var exe=Path.Combine(library,"steamapps","common",name,"QuestAway.exe");if(File.Exists(exe))return exe;
        }
        return null;
    }
}

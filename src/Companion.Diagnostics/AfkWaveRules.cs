using System.Security.Cryptography;
using System.Text.Json;

namespace Companion.Diagnostics;

internal static class AfkWaveRules
{
    internal record Rules(string GameVersion,string ExeSha256,Dictionary<string,int[]> Maps,int LevelCap,double[] XpToNext);
    static readonly Rules Data=JsonSerializer.Deserialize<Rules>(typeof(AfkWaveRules).Assembly.GetManifestResourceStream("afk-waves.json")!)!;
    internal static int LevelCap=>Data.LevelCap;
    internal static int? TotalEnemies(AfkSave? save)=>save is not null&&Data.Maps.TryGetValue($"{save.Difficulty}:{save.Act}:{save.Stage}",out var rows)?rows.Sum():null;
    internal static double? XpToNext(int level)=>level>0&&level<Data.LevelCap&&level<Data.XpToNext.Length&&double.IsFinite(Data.XpToNext[level])&&Data.XpToNext[level]>0?Data.XpToNext[level]:null;
    internal static bool MatchesExe(string exe)
    {
        try{using var file=File.OpenRead(exe);return Convert.ToHexString(SHA256.HashData(file)).Equals(Data.ExeSha256,StringComparison.OrdinalIgnoreCase);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    internal static (int Completed,int Total)? Completed(AfkSave? save)
    {
        if(save?.StageKills is not long kills||!Data.Maps.TryGetValue($"{save.Difficulty}:{save.Act}:{save.Stage}",out var sizes)||sizes.Length==0||sizes.Any(n=>n<1))return null;
        long sum=0;int completed=0;foreach(var size in sizes){sum+=size;if(kills>=sum)completed++;else break;}
        return(completed,sizes.Length);
    }
}

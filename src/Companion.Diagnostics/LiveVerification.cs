using System.Diagnostics;

namespace Companion.Diagnostics;

internal static class LiveVerification
{
    public static async Task<int> Run(string output,int seconds,CancellationToken token)
    {
        Directory.CreateDirectory(output);
        var stats=new StatisticsEngine();var snapshots=new List<object>();var deltas=new List<SampleDelta>();
        var watch=Stopwatch.StartNew();var me=Process.GetCurrentProcess();var cpu=me.TotalProcessorTime;
        while(watch.Elapsed.TotalSeconds<seconds)
        {
            var save=await SaveSource.Read(SaveSource.DefaultPath,token);
            if(save.Fingerprint!=stats.Previous?.Fingerprint)
            {
                var d=stats.Push(save);
                snapshots.Add(new{save.SavedAt,save.Version,save.ChestSerial,save.LostChests,save.ChestEquipment,save.ForgeEquipment,save.Fingerprint});
                if(d is not null)deltas.Add(d);
                Console.WriteLine($"save={save.SavedAt} chests={stats.ChestCount} equipment={stats.EquipmentCount}");
            }
            await Task.Delay(500,token);
        }
        Program.Save(output,"real-save-verification.json",new{Source="RealLocalSave",Synthetic=false,DurationSeconds=watch.Elapsed.TotalSeconds,
            CpuMs=(me.TotalProcessorTime-cpu).TotalMilliseconds,WorkingSetBytes=me.WorkingSet64,Snapshots=snapshots,Deltas=deltas,
            stats.ChestCount,stats.GeneratedChestCount,stats.LostChestCount,stats.EquipmentCount,stats.Equipment,stats.Forged,stats.HasGaps,
            PositiveLootDeltaObserved=deltas.Any(d=>d.ObtainedChests>0||d.Equipment.Values.Sum()>0),
            Notice="装备只统计 itemsFound.chest；制作单列。宝箱实际获得=生成差值−满仓损失差值；本次监测基线之前不补造。"});
        return 0;
    }
}

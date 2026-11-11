using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Companion.Diagnostics;

internal record AfkHeroRate(AfkHero Hero,double? XpPerHour);
internal record AfkTelemetry(long SavedAt,AfkSave Save,AfkHeroRate[] Heroes)
{public double? EstimatedMapSeconds{get;init;}}

internal sealed class AfkTracker
{
    SaveSnapshot? previous;readonly Dictionary<string,(double Seconds,double Xp)> rates=new();
    readonly Queue<(double Seconds,long Kills)> mapSamples=new();
    public AfkTelemetry? Current{get;private set;}
    public void Reset(){previous=null;rates.Clear();mapSamples.Clear();Current=null;}
    public void Push(SaveSnapshot snapshot)
    {
        var data=snapshot.Afk;if(data is null){Reset();return;}
        if(previous?.SavedAt==snapshot.SavedAt&&previous.Identity==snapshot.Identity)return;
        var seconds=previous is null?0:(snapshot.SavedAt-previous.SavedAt)/1000.0;
        var old=previous?.Afk;
        var reset=previous?.Identity!=snapshot.Identity||old is null||old.Act!=data.Act||old.Stage!=data.Stage||old.Difficulty!=data.Difficulty||seconds<=0||seconds>120||!old.Heroes.Select(h=>h.Key).SequenceEqual(data.Heroes.Select(h=>h.Key));
        if(reset){rates.Clear();mapSamples.Clear();}
        else if(old is not null)foreach(var hero in data.Heroes)
        {
            if(hero.Key.Length==0)continue;
            var p=old.Heroes.SingleOrDefault(h=>h.Key==hero.Key);
            if(p is null||hero.Key.Length==0||p.Level!=hero.Level||p.ClassId!=hero.ClassId||hero.Xp<p.Xp){rates.Remove(hero.Key);continue;}
            var row=rates.GetValueOrDefault(hero.Key);rates[hero.Key]=(row.Seconds+seconds,row.Xp+hero.Xp-p.Xp);
        }
        if(!reset&&old?.TotalKills is long prior&&data.TotalKills is long kills&&kills>=prior)
        {mapSamples.Enqueue((seconds,kills-prior));while(mapSamples.Count>12)mapSamples.Dequeue();}
        else mapSamples.Clear();
        var duration=mapSamples.Sum(s=>s.Seconds);var gained=mapSamples.Sum(s=>s.Kills);var total=AfkWaveRules.TotalEnemies(data);
        double? estimate=duration>=60&&total is >0&&gained>=total&&mapSamples.TakeLast(3).Sum(s=>s.Kills)>0?duration*total/gained:null;
        previous=snapshot;
        Current=new(snapshot.SavedAt,data,data.Heroes.Select(h=>new AfkHeroRate(h,rates.TryGetValue(h.Key,out var r)&&r.Seconds>0?r.Xp/r.Seconds*3600:null)).ToArray()){EstimatedMapSeconds=estimate};
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Companion.Diagnostics;

internal record GameHudReading(string Map,string Wave,DateTimeOffset ObservedAt)
{
    internal static GameHudReading? Parse(IEnumerable<(string Text,Rect Bounds)> source)
    {
        var rows=source.ToArray();var maps=rows.Select(r=>(Row:r,Match:Regex.Match(r.Text,@"(?<!\d)([1-9]|10)-([1-9]|10)(?!\d)"))).Where(x=>x.Match.Success).ToArray();
        if(maps.Select(x=>x.Match.Value).Distinct().Count()!=1)return null;
        var map=maps[0];var waves=rows.Where(r=>!r.Bounds.IsEmpty&&!map.Row.Bounds.IsEmpty&&Math.Abs(r.Bounds.Top-map.Row.Bounds.Top)<30)
            .Select(r=>Regex.Match(r.Text,@"^\s*(\d{1,3})\s*/\s*(\d{1,3})\s*$")).Where(m=>m.Success)
            .Where(m=>int.Parse(m.Groups[2].Value)>0&&int.Parse(m.Groups[1].Value)<=int.Parse(m.Groups[2].Value)).Select(m=>m.Groups[1].Value+"/"+m.Groups[2].Value).Distinct().ToArray();
        return waves.Length==1?new(map.Match.Value,waves[0],DateTimeOffset.UtcNow):null;
    }
}

internal sealed record AfkLabels(string GameVersion,Dictionary<string,string> Classes,Dictionary<string,string> Potions)
{public Dictionary<string,string> Difficulties{get;init;}=new();}
internal sealed class AfkPanel:DockPanel
{
    static readonly ControlTemplate ProgressTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
          <Border Background="#20160e" BorderBrush="#98733b" BorderThickness="1" CornerRadius="3">
            <Grid x:Name="PART_Track" ClipToBounds="True"><Border x:Name="PART_Indicator" Background="#c19848" CornerRadius="2" HorizontalAlignment="Left"/></Grid>
          </Border>
        </ControlTemplate>
        """);
    static readonly AfkLabels Labels=JsonSerializer.Deserialize<AfkLabels>(Assembly.GetExecutingAssembly().GetManifestResourceStream("afk-labels.json")!)!;
    static readonly Dictionary<string,BitmapImage?> Faces=new();
    readonly TextBlock total=Text("挂机统计",13,"#f1d69a"),clock=Text("",9.5),potion=Text("",10.5);
    readonly StackPanel body=new();RuntimeView? view;readonly List<(TextBlock Label,AfkHeroRate Hero)> estimates=new();string? lastContentKey;
    readonly TextBlock potionWarning=Text("当前没有生效的药水",12,"#ff6258");bool remindPotion;
    long potionPreviewUntil;
    internal void PreviewPotionWarning(int milliseconds=10000){potionPreviewUntil=Environment.TickCount64+Math.Clamp(milliseconds,50,10000);Tick();}
    internal event Action? MinimizeRequested;
    internal event Action? RestoreRequested;
    internal event Action? WikiRequested;
    internal event Action? SettingsRequested;
    static TextBlock Text(string value,double size=11,string color="#dfcba8")=>new(){Text=value,FontSize=size,Foreground=WikiUi.Brush(color),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,1,0,1)};
    public AfkPanel()
    {
        var header=new Grid{Margin=new Thickness(0,0,0,4)};header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.RowDefinitions.Add(new(){Height=GridLength.Auto});header.RowDefinitions.Add(new(){Height=GridLength.Auto});header.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(buttons,1);header.Children.Add(buttons);
        void Button(string label,Action click){var button=WikiUi.Button(label);button.Padding=new Thickness(5,0,5,0);button.Height=22;button.FontSize=11;button.Margin=new Thickness(3,0,0,0);button.Click+=(_,_)=>click();buttons.Children.Add(button);}
        Button("−",()=>MinimizeRequested?.Invoke());Button("恢复",()=>RestoreRequested?.Invoke());Button("wiki",()=>WikiRequested?.Invoke());
        total.FontWeight=FontWeights.SemiBold;total.Margin=new Thickness(0,2,4,0);total.TextWrapping=TextWrapping.NoWrap;total.TextTrimming=TextTrimming.CharacterEllipsis;header.Children.Add(total);SetDock(header,Dock.Top);Children.Add(header);
        SizeChanged+=(_,_)=>{var narrow=ActualWidth<340;Grid.SetRow(total,narrow?1:0);Grid.SetColumnSpan(total,narrow?2:1);};
        potionWarning.FontWeight=FontWeights.Bold;potionWarning.Visibility=Visibility.Collapsed;Grid.SetRow(potionWarning,2);Grid.SetColumnSpan(potionWarning,2);header.Children.Add(potionWarning);
        clock.ToolTip="会话经过时长包含暂停和等待；经验与已清进度按存档刷新，非逐帧战斗值。";
        var footer=new DockPanel();var gear=WikiUi.Button("⚙");gear.Width=22;gear.Height=22;gear.Padding=new Thickness(0);gear.Click+=(_,_)=>SettingsRequested?.Invoke();SetDock(gear,Dock.Right);footer.Children.Add(gear);
        var version=Text("v"+SoftwareUpdate.DisplayVersion,9.5,"#b89a5e");version.VerticalAlignment=VerticalAlignment.Center;version.Margin=new Thickness(6,0,6,0);SetDock(version,Dock.Right);footer.Children.Add(version);
        clock.TextWrapping=TextWrapping.NoWrap;clock.TextTrimming=TextTrimming.CharacterEllipsis;footer.Children.Add(clock);SetDock(footer,Dock.Bottom);Children.Add(footer);
        Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
    }
    internal static string Metric(double value)=>value>=1e8?(value/1e8).ToString("0.0")+"亿":value>=1e4?(value/1e4).ToString("0.0")+"万":value.ToString("0");
    public void Update(RuntimeView next,GameHudReading? hud,bool potionReminder=false)
    {
        view=next;remindPotion=potionReminder;var s=next.State;
        var contentKey=$"{next.Afk?.SavedAt}:{s.Previous?.Fingerprint}:{s.EquipmentAvailable}:{s.HasGaps}:{next.WaveRulesVerified}:{next.Synthetic}:{hud?.Wave}:"+string.Join(',',s.Equipment.Values);
        if(contentKey==lastContentKey){Tick();return;}lastContentKey=contentKey;
        body.Children.Clear();estimates.Clear();var known=s.EquipmentAvailable&&!s.HasGaps;
        var count=s.Equipment.Values.Sum();total.Text=(next.Synthetic?"[测试] ":"")+"本次装备产出："+(known?count.ToString():count>0?"≥"+count:"—");
        var colors=new WrapPanel();foreach(var q in SaveSource.Qualities)colors.Children.Add(new TextBlock{Text=AppResources.Text(q)+" "+(known?s.Equipment[q].ToString():s.Equipment[q]>0?"≥"+s.Equipment[q]:"—"),FontSize=10.5,Foreground=WikiUi.Brush(AppResources.Color(q)),Margin=new Thickness(0,0,10,3)});body.Children.Add(colors);
        var data=next.Afk;
        var waveProgress=next.WaveRulesVerified?AfkWaveRules.Completed(data?.Save):null;
        var wave=waveProgress is{} p?$"已清波次 {p.Completed}/{p.Total} · 存档":"已清波次：—";
        var timing=next.WaveRulesVerified?data?.EstimatedMapSeconds:null;var speed=timing is >0?"估算约 "+MapDuration(timing.Value)+"/图":"每图耗时：计算中";
        var summary=Text(MapName(data?.Save)+"  ·  "+wave+"  ·  "+speed,10.5,"#e2c17d");summary.TextWrapping=TextWrapping.NoWrap;summary.TextTrimming=TextTrimming.CharacterEllipsis;summary.ToolTip=summary.Text;summary.Tag="farming-summary";body.Children.Add(summary);
        body.Children.Add(potion);
        var heroes=new Grid();heroes.ColumnDefinitions.Add(new());heroes.ColumnDefinitions.Add(new());
        for(var i=0;i<2;i++)heroes.RowDefinitions.Add(new());
        for(var i=0;i<4;i++)
        {
            var card=new DockPanel{Margin=new Thickness(0,2,7,2)};
            var row=data?.Heroes.ElementAtOrDefault(i);
            if(row is not null)
            {
                var hero=row.Hero;
                if(!Faces.TryGetValue(hero.ClassId,out var image))
                {
                    image=null;using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("afk.face-"+hero.ClassId+".png");
                    if(stream is not null){image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();}Faces[hero.ClassId]=image;
                }
                card.Children.Add(new Image{Source=image,Width=24,Height=30,Margin=new Thickness(0,0,5,0)});
                var text=new StackPanel();text.Children.Add(Text(Labels.Classes.GetValueOrDefault(hero.ClassId,"角色")+" Lv."+hero.Level,10.5,"#e8cc92"));
                var progress=new Grid();progress.ColumnDefinitions.Add(new());progress.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                var required=next.WaveRulesVerified?AfkWaveRules.XpToNext(hero.Level):null;var capped=next.WaveRulesVerified&&hero.Level>=AfkWaveRules.LevelCap;
                var bar=new ProgressBar{Minimum=0,Maximum=1,Value=capped?1:required is double target?Math.Clamp(hero.Xp/target,0,1):0,Height=9,MinWidth=28,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,3,6,3),Template=ProgressTemplate,Opacity=required is not null||capped?1:.35};
                bar.ToolTip=required is double cost?hero.Xp.ToString("N0")+" / "+cost.ToString("N0")+" · "+(hero.Xp/cost).ToString("P1"):capped?"已满级":"当前版本经验表未确认";progress.Children.Add(bar);
                var eta=Text("计算中",9.5,"#aecc83");eta.MaxWidth=112;eta.ToolTip="按当前分段平均经验速率估算，换图、药水到期或升级会改变时间。";Grid.SetColumn(eta,1);progress.Children.Add(eta);estimates.Add((eta,row));text.Children.Add(progress);
                text.ToolTip=row.XpPerHour is double rate?"本段经验速率 "+Metric(rate)+" / 小时":"等待经验样本";card.Children.Add(text);
            }
            else card.Children.Add(Text("角色 "+(i+1)+"：等待存档",10));
            Grid.SetColumn(card,i%2);Grid.SetRow(card,i/2);heroes.Children.Add(card);
        }
        body.Children.Add(heroes);Tick();
    }
    internal static string UpgradeEstimate(AfkHeroRate hero,bool verified,long savedAt,DateTimeOffset now)
    {
        if(!verified)return "计算中";
        if(hero.Hero.Level>=AfkWaveRules.LevelCap)return "已满级";
        if(AfkWaveRules.XpToNext(hero.Hero.Level) is not double required||hero.XpPerHour is not >0||now.ToUnixTimeMilliseconds()-savedAt is <0 or >75000)return "计算中";
        var seconds=(required-hero.Hero.Xp)/hero.XpPerHour.Value*3600-(now.ToUnixTimeMilliseconds()-savedAt)/1000.0;
        if(!double.IsFinite(seconds)||seconds>365d*86400)return "暂无法预计";
        if(seconds<=0)return "等待更新";
        var minutes=Math.Ceiling(seconds/60);if(minutes<60)return "约"+minutes.ToString("0")+"分后升级";
        if(minutes<1440)return "约"+Math.Floor(minutes/60).ToString("0")+"小时"+(minutes%60>0?(minutes%60).ToString("0")+"分":"")+"后升级";
        var hours=Math.Ceiling(minutes/60);return "约"+Math.Floor(hours/24).ToString("0")+"天"+(hours%24>0?(hours%24).ToString("0")+"小时":"")+"后升级";
    }
    internal static string PotionText(AfkTelemetry? data,DateTimeOffset now)
    {
        if(data?.Save.Potions is null)return "药水：—";
        var age=now.ToUnixTimeMilliseconds()-data.SavedAt;
        if(age<0||age>75000)return "药水：等待新存档";
        if(data.Save.Potions.Length==0)return "药水：无生效药水";
        return string.Join(" · ",data.Save.Potions.Select(p=>Labels.Potions.GetValueOrDefault(p.Id,"药水")+" 约"+OverlayWindow.SessionDuration(DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch.AddMilliseconds(Math.Max(0,p.RemainingMs-age)))));
    }
    internal static string MapName(AfkSave? save)=>save is{Act:not null,Stage:not null,Difficulty:not null}?Labels.Difficulties.GetValueOrDefault(save.Difficulty.Value.ToString(),"未知难度")+$" {save.Act}-{save.Stage}":"地图：—";
    internal static string MapDuration(double seconds){var n=(long)Math.Ceiling(seconds);return n<60?n+"秒":n/60+"分"+n%60+"秒";}
    internal static bool NoActivePotion(AfkTelemetry? data,DateTimeOffset now)=>data?.Save.Potions is{} rows&&now.ToUnixTimeMilliseconds()-data.SavedAt is >=0 and <=75000&&rows.All(p=>p.RemainingMs<=0);
    public void Tick()
    {
        if(view is null)return;potion.Text=PotionText(view.Afk,DateTimeOffset.UtcNow);potion.ToolTip="按最后存档的剩余毫秒估算；游戏暂停或存档过期时以新存档为准。";
        var preview=Environment.TickCount64<potionPreviewUntil;potionWarning.Text=(preview?"[测试] ":"")+"当前没有生效的药水";
        potionWarning.Visibility=preview||remindPotion&&view.State.EquipmentAvailable&&NoActivePotion(view.Afk,DateTimeOffset.UtcNow)?Visibility.Visible:Visibility.Collapsed;
        foreach(var estimate in estimates)estimate.Label.Text=UpgradeEstimate(estimate.Hero,view.WaveRulesVerified,view.Afk?.SavedAt??0,DateTimeOffset.UtcNow);
        clock.Text="本次记录 "+OverlayWindow.SessionDuration(view.StartTime,DateTimeOffset.UtcNow)+"  · 保存 "+OverlayWindow.SavedClock(view.Afk?.SavedAt);
    }
}

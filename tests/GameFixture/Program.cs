using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

// Development-only synthetic window and synthetic save emitter. Never opens or controls QuestAway.
internal static class Program
{
    [DllImport("user32.dll")]static extern nint GetForegroundWindow();
    [STAThread]
    static void Main(string[] args)
    {
        var directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
        var app=new Application();var window=new Window{Title="QATK integration fixture — SYNTHETIC, NOT QUESTAWAY",Width=1000,Height=650,Left=80,Top=100,
            Background=new SolidColorBrush(Color.FromRgb(57,34,16)),Content=new TextBlock{Text="SYNTHETIC WINDOW\nNo real game input or loot.\nMoves, minimizes, restores, then exits automatically.",Foreground=Brushes.Ivory,FontSize=24,Margin=new Thickness(30)}};
        Window? popup=null;
        void Mark(string name)=>File.AppendAllText(Path.Combine(directory,"window-timeline.jsonl"),JsonSerializer.Serialize(new{AtUtc=DateTimeOffset.UtcNow,Event=name,State=window.WindowState.ToString(),
            MainHwnd=$"0x{new WindowInteropHelper(window).Handle:X}",PopupHwnd=popup is null?null:$"0x{new WindowInteropHelper(popup).Handle:X}",Foreground=$"0x{GetForegroundWindow():X}"})+"\n");
        void Save(int stage)
        {
            var qualities=new[]{"common","uncommon","rare","heroic","mythic","ascendant","eternal"};
            var chests=qualities.ToDictionary(q=>q,q=>q=="common"?(stage==0?10:stage<3?13:14):0);
            var forge=qualities.ToDictionary(q=>q,q=>q=="common"?(stage<2?3:6):0);
            object Item(string id)=>new{id,baseType="wpn_knight",rarity="common",itemLevel=10};
            var inventory=new List<object>{Item("synthetic-old")};if(stage>=1)inventory.Add(Item("synthetic-new-a"));if(stage>=2)inventory.Add(Item("synthetic-crafted"));if(stage>=3)inventory.Add(Item("synthetic-new-b"));
            var moved=stage>=2?new[]{inventory[0]}:Array.Empty<object>();if(stage>=2)inventory.RemoveAt(0);
            var raw=new{savedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),temporada=1,game=new{version=22,game=new{playerId="SYNTHETIC-ONLY-NO-ACCOUNT",
                sim=new{chestSerial=stage==0?100:stage<3?105:106,bausPerdidos=stage==0?5:6,inventory,stash=moved},itemsFound=new{chest=chests,forge},heroes=Array.Empty<object>(),bench=Array.Empty<object>()}}};
            var path=Path.Combine(directory,"synthetic-save.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(raw));File.Move(path+".tmp",path,true);
            File.AppendAllText(Path.Combine(directory,"fixture-timeline.jsonl"),JsonSerializer.Serialize(new{AtUtc=DateTimeOffset.UtcNow,Stage=stage,raw.savedAt})+"\n");
        }
        window.ContentRendered+=async (_,_)=>
        {
            Save(0);File.WriteAllText(Path.Combine(directory,"ready.txt"),"synthetic only");
            await Task.Delay(4500);Save(1);
            await Task.Delay(3500);Save(2);window.Left+=120;window.Top+=80;window.Width=850;Mark("moved");
            if(args.Contains("--owned-popup"))
            {
                popup=new Window{Owner=window,Title="SYNTHETIC owned popup — NOT GAME",Width=420,Height=280,Left=window.Left+70,Top=window.Top+60,ShowInTaskbar=false,
                    Background=Brushes.BurlyWood,Content=new TextBlock{Text="Own test popup.\nOverlay must remain attached to owner.",Margin=new Thickness(24),FontSize=18}};
                popup.Show();popup.Activate();await Task.Delay(150);Mark("popup_foreground");
            }
            await Task.Delay(3500);Save(3);
            await Task.Delay(2000);window.WindowState=WindowState.Minimized;Mark("minimized");
            await Task.Delay(2500);window.WindowState=WindowState.Normal;Mark("restored");
            if(popup is not null){await Task.Delay(150);popup.Activate();Mark("popup_foreground_restored");}
            await Task.Delay(3000);Mark("closing");popup?.Close();window.Close();
        };
        app.Run(window);
    }
}

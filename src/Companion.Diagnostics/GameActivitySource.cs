using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Companion.Diagnostics;

internal static class GameActivitySource
{
    const int TextLimit=250000;
    static readonly Regex MinutePattern=new(@"^(?:[01]?\d|2[0-3]):[0-5]\d$",RegexOptions.CultureInvariant);
    static readonly Regex BatchPattern=new(@"^开启了[1-9][0-9]{0,2}个宝箱：",RegexOptions.CultureInvariant);
    static readonly Regex RepeatedItemPattern=new(@"^(?<name>.+) ×(?<count>[1-9][0-9]{0,2})$",RegexOptions.CultureInvariant);
    static readonly Regex ClippedGroupsPattern=new(@" \(\+[1-9][0-9]*\)$",RegexOptions.CultureInvariant);

    // Reuses the stage-two reader: exact owned log document; never focus, invoke, scroll or input.
    public static ActivitySnapshot Read(ProcessObservation game,IReadOnlyDictionary<string,string> qualityLabels)
    {
        using var process=Process.GetProcessById(game.Pid);
        if(game.StartUtc is null||process.StartTime.ToUniversalTime()!=game.StartUtc.Value||
            !process.ProcessName.Equals(Path.GetFileNameWithoutExtension(game.Exe),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("activity_process_changed");
        var windows=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,game.Pid));
        if(windows.Count is 0 or >64)throw new InvalidDataException("activity_owned_windows_unavailable");
        var documents=new Dictionary<string,AutomationElement>();
        foreach(AutomationElement window in windows)
        {
            var children=window.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document));
            if(children.Count>64)throw new InvalidDataException("activity_document_limit");
            foreach(AutomationElement doc in children)
                // WebView2 document providers may belong to a child renderer PID; ownership is the verified game window above.
                if(doc.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&
                    ((ValuePattern)value).Current.Value=="http://tauri.localhost/log.html")
                    documents.TryAdd(string.Join(":",doc.GetRuntimeId()),doc);
        }
        if(documents.Count!=1)throw new InvalidDataException("activity_log_document_ambiguous");
        var selected=documents.Single();
        var text=((TextPattern)selected.Value.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(TextLimit);
        if(text.Length>=TextLimit)throw new InvalidDataException("activity_text_truncated");
        if(process.HasExited||process.StartTime.ToUniversalTime()!=game.StartUtc.Value)throw new InvalidDataException("activity_process_changed");
        return Parse(text,DateTimeOffset.UtcNow,selected.Key,qualityLabels);
    }

    public static ActivitySnapshot Parse(string text,DateTimeOffset observed,string documentId,IReadOnlyDictionary<string,string> qualityLabels)
    {
        if(text.Length>=TextLimit)throw new InvalidDataException("activity_text_truncated");
        string? minute=null;var notices=new List<ActivityNotice>();
        foreach(var line in text.Split('\n'))
        {
            var t=line.Trim();
            if(MinutePattern.IsMatch(t)){minute=t;continue;}
            if(t.StartsWith("开启了",StringComparison.Ordinal)&&t.Contains("个宝箱",StringComparison.Ordinal))
            {
                if(minute is null||t.Length>200||t.Any(char.IsControl))throw new InvalidDataException("activity_notice_unframed");
                var prefix=BatchPattern.Match(t);var incomplete=!prefix.Success;int part=0;
                if(prefix.Success)foreach(var group in t[prefix.Length..].Split(", ",StringSplitOptions.None))
                {
                    string? itemName=null,itemQuality=null;int quantity=1;
                    // (+N) counts hidden display groups, not known equipment.
                    var clip=ClippedGroupsPattern.Match(group);if(clip.Success)incomplete=true;
                    var visibleGroup=clip.Success?group[..clip.Index]:group;
                    if(!visibleGroup.Contains("(+",StringComparison.Ordinal))foreach(var label in qualityLabels)
                        if(visibleGroup.StartsWith(label.Key+" ",StringComparison.Ordinal))
                        {
                            var candidate=visibleGroup[(label.Key.Length+1)..].Trim();var repeat=RepeatedItemPattern.Match(candidate);
                            if(repeat.Success){quantity=int.Parse(repeat.Groups["count"].Value,System.Globalization.CultureInfo.InvariantCulture);candidate=repeat.Groups["name"].Value;}
                            if(candidate.Length>0&&!candidate.Contains('×')&&!candidate.Contains('＊')&&!candidate.Contains(',')){itemName=candidate;itemQuality=label.Value;}
                            break;
                        }
                    if(itemName is null||itemQuality is null){incomplete=true;continue;}
                    if(notices.Count+quantity>1000)throw new InvalidDataException("activity_notice_limit");
                    for(int i=0;i<quantity;i++)notices.Add(new(minute,t,"ChestOpened",itemName,itemQuality){Part=++part});
                }
                if(incomplete)notices.Add(new(minute,t,"ChestEquipmentUnconfirmed"));
                if(notices.Count>1000)throw new InvalidDataException("activity_notice_limit");
                continue;
            }
            var kind=t=="掉落了一个宝箱"?"ChestObtained":t.StartsWith("开启了宝箱：",StringComparison.Ordinal)?"ChestOpened":
                t.StartsWith("巨像宝箱：",StringComparison.Ordinal)?"ColossusReward":
                t.StartsWith("转化完成：",StringComparison.Ordinal)?"TransmutationCompleted":null;
            if(kind is null)continue; // Other-player notices and unrelated UI text never leave this reader.
            if(minute is null||t.Length>200||t.Any(char.IsControl))throw new InvalidDataException("activity_notice_unframed");
            string? name=null,quality=null;
            if(kind!="ChestObtained")
            {
                var payload=t[(t.IndexOf('：')+1)..];
                foreach(var label in qualityLabels)
                    if(payload.StartsWith(label.Key+" ",StringComparison.Ordinal))
                    {
                        var candidate=payload[(label.Key.Length+1)..].Trim();
                        if(candidate.Length>0&&!candidate.Contains('×')&&!candidate.Contains('＊')){name=candidate;quality=label.Value;}
                        else kind="ChestEquipmentUnconfirmed";
                        break;
                    }
            }
            notices.Add(new(minute,t,kind,name,quality));
            if(notices.Count>1000)throw new InvalidDataException("activity_notice_limit");
        }
        return new(observed,documentId,minute,notices.ToArray());
    }
}



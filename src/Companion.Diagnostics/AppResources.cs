using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Companion.Diagnostics;

internal static class AppResources
{
    static JsonDocument Load(string name)=>JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream(name)??throw new InvalidOperationException("Missing resource "+name));
    static readonly JsonDocument Ui=Load("ui-text.json"),Quality=Load("quality-reference.json"),Items=Load("item-names.json");
    public static string[] Languages=>Ui.RootElement.GetProperty("languages").EnumerateArray().Select(v=>v.GetString()!).ToArray();
    public static string Language{get;set;}=DefaultLanguage();
    static string DefaultLanguage()
    {
        var name=CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh",StringComparison.OrdinalIgnoreCase)?(name.Contains("TW")||name.Contains("HK")||name.Contains("Hant")?"zh-Hant":"zh-Hans"):
            name.StartsWith("ja")?"ja":name.StartsWith("es")?"es":"en";
    }
    public static string Text(string key)=>Ui.RootElement.GetProperty("text").TryGetProperty(key,out var values)?values[Math.Max(0,Array.IndexOf(Languages,Language))].GetString()!:Text("unknown");
    public static string Color(string quality)=>Quality.RootElement.GetProperty("qualities").EnumerateArray().First(q=>q.GetProperty("id").GetString()==quality).GetProperty("nativeColor").GetString()!;
    public static IReadOnlyDictionary<string,string> ActivityQualityLabels=>Quality.RootElement.GetProperty("qualities").EnumerateArray().ToDictionary(q=>q.GetProperty("zhHans").GetString()!,q=>q.GetProperty("id").GetString()!);
    public static string ItemName(ObservedItem item)=>Items.RootElement.GetProperty("names").TryGetProperty(item.BaseType,out var label)?label.GetProperty(Language).GetString()!:item.BaseType;
    public static void Validate()
    {
        foreach(var pair in Ui.RootElement.GetProperty("text").EnumerateObject())
            if(pair.Value.GetArrayLength()!=5||pair.Value.EnumerateArray().Any(v=>string.IsNullOrWhiteSpace(v.GetString())))throw new InvalidDataException("Incomplete translation "+pair.Name);
        foreach(var q in SaveSource.Qualities){_ = Color(q);_ =Text(q);}
        foreach(var item in Items.RootElement.GetProperty("names").EnumerateObject())
            foreach(var language in Languages)if(!item.Value.TryGetProperty(language,out var value)||string.IsNullOrWhiteSpace(value.GetString()))throw new InvalidDataException("Incomplete item label "+item.Name);
    }
}

internal sealed class CompanionSettings
{
    public string Language{get;set;}=AppResources.Language;
    public string GameExe{get;set;}="";
    public string SavePath{get;set;}="";
    public double Opacity{get;set;}=.88;
    public double Scale{get;set;}=1;
    public int OffsetX{get;set;}=12;
    public int OffsetY{get;set;}=16;
    public bool ClickThrough{get;set;}=true;
    public bool Expanded{get;set;}
    public string DockSide{get;set;}="right";
    public string WikiSide{get;set;}="top";
    public bool ColossusReminderEnabled{get;set;}
    public bool PotionReminderEnabled{get;set;}
    public int ColossusReminderMinutes{get;set;}=1;
    public string[] ColossusReminderSent{get;set;}=[];
    [System.Text.Json.Serialization.JsonIgnore]public string ColossusReminderStatus{get;set;}="";
    [System.Text.Json.Serialization.JsonIgnore]internal string? StorageDirectory{get;set;}
    public static string DataDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuestAwayCompanion");
    static string SettingsPath=>Path.Combine(DataDirectory,"settings.json");
    public static CompanionSettings Load(string? directory=null)
    {
        CompanionSettings settings;
        try{settings=JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(directory is null?SettingsPath:Path.Combine(directory,"settings.json")))??new();}catch(IOException){settings=new();}catch(JsonException){settings=new();}
        settings.StorageDirectory=directory;
        settings.Opacity=double.IsFinite(settings.Opacity)?Math.Clamp(settings.Opacity,.3,1):.88;
        settings.Scale=double.IsFinite(settings.Scale)?Math.Clamp(settings.Scale,.65,2):1;
        if(!AppResources.Languages.Contains(settings.Language))settings.Language="en";
        if(!WikiPopupPlacement.Sides.Contains(settings.DockSide))settings.DockSide="right";
        if(!WikiPopupPlacement.Sides.Contains(settings.WikiSide))settings.WikiSide="top";
        if(settings.ColossusReminderMinutes is not (1 or 5))settings.ColossusReminderMinutes=1;
        settings.ColossusReminderSent=(settings.ColossusReminderSent??[]).Where(s=>s is not null&&s.Length<=64).TakeLast(32).ToArray();
        return settings;
    }
    public void Save()
    {
        var directory=StorageDirectory??DataDirectory;var path=Path.Combine(directory,"settings.json");Directory.CreateDirectory(directory);var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,Program.Json));File.Move(temp,path,true);
    }
}

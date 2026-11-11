using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Companion.Diagnostics;

internal static class StoreInspection
{
    public static void Run(string path,string output)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA integrity_check";var integrity=cmd.ExecuteScalar();
        cmd.CommandText="SELECT SessionId,StartTime,EndTime,ChestCount,EquipmentCount,StateJson,EndReason,ProcessKey FROM Sessions ORDER BY StartTime";
        var rows=new List<object>();
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var state=JsonSerializer.Deserialize<StatisticsState>(r.GetString(5),Program.Json)!;
            rows.Add(new{SessionId=r.GetString(0),StartTime=r.GetString(1),EndTime=r.IsDBNull(2)?null:r.GetString(2),
                ChestCount=r.GetInt64(3),EquipmentCount=r.GetInt64(4),ForgeCount=state.Forged.Values.Sum(),EndReason=r.GetString(6),
                Source=r.GetString(7).StartsWith("synthetic:")?"Synthetic":r.GetString(7).StartsWith("real:")?"RealLocalSave":"Unknown"});
        }
        cmd.CommandText="SELECT COUNT(*) FROM Events";var events=(long)cmd.ExecuteScalar()!;
        Program.Save(output,"store-inspection.json",new{Integrity=integrity,EventCount=events,Sessions=rows});
    }
}

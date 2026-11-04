using System.Text.Json;
using System.Globalization;

namespace Codex.SpeedMetrics;
public sealed record MonthlyApiValue(string Month,long Tokens,decimal KnownApiUsd,int Responses,int UnpricedResponses,bool Closed);
public static class MonthlyApiArchive
{
    public static IReadOnlyList<MonthlyApiValue> Update(IEnumerable<MonthlyApiValue> previous,IEnumerable<UsageEntry> entries,DateOnly today)
    {
        var current=today.ToString("yyyy-MM",CultureInfo.InvariantCulture);
        var rows=previous.ToDictionary(x=>x.Month,StringComparer.Ordinal);
        foreach(var group in entries.DistinctBy(e=>e.Id).GroupBy(e=>e.At.UtcDateTime.ToString("yyyy-MM",CultureInfo.InvariantCulture)))
        {
            if(rows.TryGetValue(group.Key,out var old)&&old.Closed&&old.UnpricedResponses==0)continue;
            var sum=UsageTotals.Sum(group);
            rows[group.Key]=new(group.Key,sum.Total,sum.KnownApiUsd,sum.Responses,sum.UnpricedResponses,
                string.CompareOrdinal(group.Key,current)<0);
        }
        foreach(var key in rows.Keys.ToArray())
            if(string.CompareOrdinal(key,current)<0)rows[key]=rows[key] with {Closed=true};
        return rows.Values.OrderBy(x=>x.Month,StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<MonthlyApiValue> Save(string path,IEnumerable<UsageEntry> entries,DateOnly today)
    {
        var previous=File.Exists(path)?JsonSerializer.Deserialize<MonthlyApiValue[]>(File.ReadAllText(path))??Array.Empty<MonthlyApiValue>():Array.Empty<MonthlyApiValue>();
        var updated=Update(previous,entries,today);
        var content=JsonSerializer.Serialize(updated,new JsonSerializerOptions{WriteIndented=true});
        if(!File.Exists(path)||File.ReadAllText(path)!=content)
        {Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temporary,content);File.Move(temporary,path,true);}
        return updated;
    }
}

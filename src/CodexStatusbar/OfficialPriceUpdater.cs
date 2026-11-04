using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.SpeedMetrics;

namespace CodexStatusbar;
internal sealed class OfficialPriceUpdater
{
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(15)};
    private readonly Dictionary<string,DateTimeOffset> _next=new(StringComparer.Ordinal);
    private readonly string _path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexStatusbar","api-prices.json");
    private bool _loaded;
    public string Status {get;private set;}="价格：2026-11-01 备用表，待官方核对";
    public async Task Refresh(IEnumerable<string> models,bool force,CancellationToken token)
    {
        var prices=ApiEquivalent.Prices.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        if(!_loaded)
        {
            _loaded=true;
            try
            {
                if(File.Exists(_path))foreach(var p in JsonSerializer.Deserialize<List<ModelPrice>>(await File.ReadAllTextAsync(_path,token))??new())
                    if(p.Source=="https://developers.openai.com/api/docs/models/"+p.Model && p.Input>=0 && p.Output>=0)
                    {prices[p.Model]=p;_next[p.Model]=p.CheckedAt.AddHours(1);}
            }
            catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException){}
            ApiEquivalent.SetPrices(prices.Values);
        }
        int failures=0;bool changed=false;var now=DateTimeOffset.UtcNow;
        foreach(var model in models.Distinct(StringComparer.Ordinal))
        {
            if(!Regex.IsMatch(model,@"^[a-z0-9][a-z0-9.-]{0,99}$"))continue;
            if(!force&&_next.GetValueOrDefault(model)>now)continue;
            _next[model]=now.AddHours(1);
            try
            {
                var url="https://developers.openai.com/api/docs/models/"+model+".md";
                using var response=await Http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
                response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength is > 524288)throw new InvalidDataException("Pricing page too large");
                var markdown=await response.Content.ReadAsStringAsync(token);
                if(markdown.Length>524288)throw new InvalidDataException("Pricing page too large");
                prices[model]=ModelPriceParser.Parse(model,markdown,DateTimeOffset.UtcNow);changed=true;
            }
            catch(Exception e)when(e is HttpRequestException or FormatException or InvalidDataException or TaskCanceledException)
            {if(token.IsCancellationRequested)throw;failures++;}
        }
        if(changed)
        {
            ApiEquivalent.SetPrices(prices.Values);
            try{Directory.CreateDirectory(Path.GetDirectoryName(_path)!);await File.WriteAllTextAsync(_path+".tmp",JsonSerializer.Serialize(prices.Values),token);File.Move(_path+".tmp",_path,true);}
            catch(IOException){}
        }
        var verified=prices.Values.Where(p=>!p.BuiltIn).ToArray();
        var fresh=verified.Where(p=>now-p.CheckedAt<TimeSpan.FromHours(2)).ToArray();
        Status=verified.Length==0?"价格：使用已注明日期的备用价；官方尚未核对"
            :$"官方价格核对 {verified.Max(p=>p.CheckedAt).ToLocalTime():MM-dd HH:mm} · 每小时更新";
        if(failures>0||fresh.Length<verified.Length)Status+=" · 部分价格为缓存或未知";
    }
}

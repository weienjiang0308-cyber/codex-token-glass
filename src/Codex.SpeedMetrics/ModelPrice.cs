using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.SpeedMetrics;
public sealed record ModelPrice(string Model,decimal Input,decimal? Cached,decimal Output,long? LongThreshold,
    decimal? LongInputMultiplier,decimal? LongCachedMultiplier,decimal? LongOutputMultiplier,
    DateTimeOffset CheckedAt,string Source,bool BuiltIn=false)
{
    public static ModelPrice Baseline(string model,decimal input,decimal cached,decimal output)=>new(model,input,cached,output,
        272000,2,2,1.5m,new DateTimeOffset(2026,11,1,0,0,0,TimeSpan.Zero),
        "https://developers.openai.com/api/docs/models/"+model,true);
}

/// <summary>Accept only the official model page's explicit USD / 1M-token pricing table.
/// A changed or ambiguous schema fails closed; no invented model aliases or prices.</summary>
public static class ModelPriceParser
{
    public static ModelPrice Parse(string model,string markdown,DateTimeOffset checkedAt)
    {
        if(!Regex.IsMatch(model,@"^[a-z0-9][a-z0-9.-]{0,99}$"))throw new FormatException("Invalid model slug");
        decimal? Rate(string name)
        {
            var matches=Regex.Matches(markdown,@"^\|\s*"+Regex.Escape(name)+@"\s*\|\s*\$([\d.]+)\s*\|\s*1M tokens\s*\|",RegexOptions.Multiline|RegexOptions.IgnoreCase);
            if(matches.Count!=1)return null;
            return decimal.TryParse(matches[0].Groups[1].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)&&value>=0?value:null;
        }
        var input=Rate("Input")??throw new FormatException("No unique per-million input rate");
        var output=Rate("Output")??throw new FormatException("No unique per-million output rate");
        var cached=Rate("Cached input");
        long? threshold=null;decimal? li=null,lc=null,lo=null;
        var rule=markdown.Split('\n').FirstOrDefault(line=>line.Contains("input tokens",StringComparison.OrdinalIgnoreCase)
            &&line.Contains("priced at",StringComparison.OrdinalIgnoreCase)
            &&!Regex.IsMatch(line,@"^\s*[-*]?\s*Cached input tokens",RegexOptions.IgnoreCase));
        if(rule is not null)
        {
            var m=Regex.Match(rule,@"(?:more than|>)\s*([\d,.]+)\s*([KM]?)\s*input tokens",RegexOptions.IgnoreCase);
            if(!m.Success)throw new FormatException("Unrecognized long-context threshold");
            var value=decimal.Parse(m.Groups[1].Value,NumberStyles.Number,CultureInfo.InvariantCulture);
            threshold=(long)(value*(m.Groups[2].Value.ToUpperInvariant() switch{"K"=>1000,"M"=>1000000,_=>1}));
            decimal? Factor(string term){var f=Regex.Match(rule,@"([\d.]+)x\s+"+term,RegexOptions.IgnoreCase);return f.Success?decimal.Parse(f.Groups[1].Value,CultureInfo.InvariantCulture):null;}
            li=Factor("input");lo=Factor("output");
            lc=rule.Contains("input and cache",StringComparison.OrdinalIgnoreCase)?li:Factor("cache");
        }
        return new(model,input,cached,output,threshold,li,lc,lo,checkedAt,
            "https://developers.openai.com/api/docs/models/"+model);
    }
}

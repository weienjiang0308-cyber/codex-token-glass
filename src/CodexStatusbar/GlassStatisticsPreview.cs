using System.Drawing.Imaging;
using System.Text.Json;
using Codex.SpeedMetrics;

namespace CodexStatusbar;
internal static class GlassStatisticsPreview
{
    public static int Run(string output)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        Directory.CreateDirectory(output);
        using var service=new UsageStatisticsService(SessionPathResolver.Resolve(),background:false);
        service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        var thread=service.Current!.Entries.OrderBy(e=>e.At).LastOrDefault()?.Thread ?? "preview";
        var entries=service.Current!.Entries.Where(e=>e.Thread==thread).ToArray();
        var totals=UsageTotals.Sum(entries);
        var selected=new TokenSnapshot(thread,"",totals.Total,totals.Input,totals.Cached,totals.Output,totals.Reasoning,
            120000,1050000,DateTime.UtcNow,TurnId:entries.LastOrDefault()?.Turn);
        var report=new List<object>();
        foreach(var theme in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
        foreach(var scale in new[]{1f,1.25f,1.5f})
        {
            using var form=new StatisticsForm(service,()=>selected,()=>OverlayThemePalette.For(theme));
            using var image=form.Preview(scale);
            var file=$"statistics-{theme}-{(int)(scale*100)}.png";
            image.Save(Path.Combine(output,file),ImageFormat.Png);
            int partial=0,transparent=0;
            for(int y=0;y<Math.Min(64,image.Height);y++)for(int x=0;x<Math.Min(64,image.Width);x++)
            {var a=image.GetPixel(x,y).A;if(a==0)transparent++;else if(a<255)partial++;}
            report.Add(new{file,scale,cornerPartialAlphaPixels=partial,cornerTransparentPixels=transparent});
        }
        File.WriteAllText(Path.Combine(output,"render-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Rendered 6 production statistics surfaces at 100%, 125%, 150%, light/dark.");return 0;
    }
}

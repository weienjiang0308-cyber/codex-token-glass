using Codex.SpeedMetrics;

namespace CodexStatusbar;
internal static class StatisticsPublicDemo
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var settings=Path.Combine(Path.GetFullPath(output),"isolated-ui.json");
        File.WriteAllText(settings,"{\"language\":\"zh\",\"glassTransparency\":0}");
        Environment.SetEnvironmentVariable("CODEX_STATUSBAR_UI_SETTINGS",settings);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var service=new UsageStatisticsService(output,background:false);
        var now=DateTimeOffset.UtcNow;
        var entries=Enumerable.Range(0,18).Select(i=>new UsageEntry("demo-"+i,"chat-"+(i%3),"turn",now.AddDays(-(i%4)),
            "gpt-6.1-sol",200000+i*5000,160000,0,3000+i*100,1200)).ToArray();
        var names=new Dictionary<string,string>{{"chat-0","界面修复与交互优化"},{"chat-1","文档整理和内容审查"},{"chat-2","API 接口开发与调试"}};
        typeof(UsageStatisticsService).GetProperty("Current")!.SetValue(service,
            new StatisticsSnapshot(entries,new Dictionary<DateOnly,long>(),null,now,null,0,0,"合成数据演示",names));
        var selected=new TokenSnapshot("chat-0","",0,0,0,0,0,0,0,now.UtcDateTime,TurnId:"turn");
        using var form=new StatisticsForm(service,()=>selected,()=>OverlayThemePalette.For(OverlayThemeKind.Light));
        for(var tab=0;tab<5;tab++)using(var bitmap=form.Preview(1,tab))bitmap.Save(Path.Combine(output,$"statistics-{tab}.png"));
        return 0;
    }
}

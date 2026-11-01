using System.Drawing.Drawing2D;

namespace CodexStatusbar;

/// <summary>Reproducible README picture using synthetic data and a drawn scene, never the desktop.</summary>
internal static class PublicDemo
{
    public static int Render(string output)
    {
        Directory.CreateDirectory(output);
        var previous=CodexUi.DemoLanguage;
        try
        {
            CodexUi.DemoLanguage="zh";
            var snapshot=new TokenSnapshot("synthetic-demo","",248000,240000,224000,8000,2000,
                62000,475000,DateTime.UnixEpoch,Title:"Synthetic example",Tps:128,PeakTps:164,AverageTps:122,
                TurnElapsedMs:95000,MergedModelElapsedMs:65000,TtftMs:1200);
            var fields=DisplayField.Total|DisplayField.Input|DisplayField.Output|DisplayField.CacheHit|DisplayField.CacheMiss|
                DisplayField.CacheHitRate|DisplayField.Context|DisplayField.ContextPercent|DisplayField.PeakSpeed|
                DisplayField.AverageSpeed|DisplayField.TurnDuration|DisplayField.ModelTime|DisplayField.FirstTokenTime;
            var presentation=OverlayPresentationBuilder.Create(snapshot,DisplayField.Tps,DisplayField.Total,fields);
            var panelHeight=212+presentation.ExpandedRows.Count*28;
            var size=new Size(444,panelHeight+64);
            using var scene=new Bitmap(size.Width,size.Height,System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using(var g=Graphics.FromImage(scene))
            {
                using var background=new LinearGradientBrush(new Rectangle(Point.Empty,size),Color.FromArgb(247,240,230),Color.FromArgb(227,233,238),45);
                g.FillRectangle(background,new Rectangle(Point.Empty,size));
                using var accent=new SolidBrush(Color.FromArgb(198,97,63));g.FillEllipse(accent,-90,20,260,260);
                using var ink=new SolidBrush(Color.FromArgb(102,126,136));g.FillEllipse(ink,300,panelHeight-100,260,260);
            }
            using var form=new TokenStripForm(clickToExpand:true);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));form.SetPresentation(presentation);
            form.ApplyLayout(new OverlayLayoutResult(OverlayVisualState.Expanded,CollapsedDisplayMode.TwoFields,
                ExpansionDirection.Up,96,new IntRect(0,0,size.Width,size.Height),
                new IntRect(210,panelHeight+28,222,22),new IntRect(12,12,420,panelHeight),28));
            form.UseSyntheticBackdrop(scene,96);
            using var image=form.RenderSurfaceBitmap(size,96);
            using var composite=new Bitmap(size.Width,size.Height);
            using(var g=Graphics.FromImage(composite)){g.DrawImageUnscaled(scene,0,0);g.DrawImageUnscaled(image,0,0);}
            composite.Save(Path.Combine(output,"demo-synthetic.png"),System.Drawing.Imaging.ImageFormat.Png);
            Console.Out.WriteLine("Rendered synthetic example; no user conversations or desktop were read for the scene.");return 0;
        }
        finally{CodexUi.DemoLanguage=previous;}
    }
}

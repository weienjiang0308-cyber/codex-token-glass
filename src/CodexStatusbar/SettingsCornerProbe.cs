using System.Drawing.Imaging;
using System.Reflection;

namespace CodexStatusbar;
internal static class SettingsCornerProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var preferences=Path.Combine(Path.GetFullPath(directory),"isolated-ui.json");
        File.WriteAllText(preferences,"{\"language\":\"zh\",\"glassTransparency\":0}");
        Environment.SetEnvironmentVariable("CODEX_STATUSBAR_UI_SETTINGS",preferences);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new SettingsForm(()=>OverlayThemePalette.For(OverlayThemeKind.Light),()=>997500);
        var close=form.Controls.OfType<GlassButton>().Single(c=>c.Text=="×");
        foreach(Control control in form.Controls)if(control!=close)control.Enabled=false;
        using var timer=new System.Windows.Forms.Timer{Interval=900};var phase=0;var mismatch=0;
        timer.Tick+=(_,_)=>
        {
            using var screenshot=new Bitmap(120,110,PixelFormat.Format32bppRgb);
            using(var g=Graphics.FromImage(screenshot))g.CopyFromScreen(form.Right-120,form.Top,0,0,screenshot.Size);
            var state=phase==0?"idle":phase==1?"hover":"pressed";
            screenshot.Save(Path.Combine(directory,"corner-"+state+".png"));
            if(typeof(SettingsForm).GetField("_materialFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form) is Bitmap material)
            {
                material.Save(Path.Combine(directory,"material-"+state+".png"));
                var offset=form.ClientSize.Width-120;
                for(var y=close.Top;y<close.Bottom;y++)for(var x=close.Left;x<close.Right;x++)
                {
                    if(x>=close.Left+2&&x<close.Right-2&&y>=close.Top+2&&y<close.Bottom-2)continue;
                    var expected=material.GetPixel(x,y);if(expected.A<254)continue;
                    var actual=screenshot.GetPixel(x-offset,y);
                    if(Math.Max(Math.Abs(expected.R-actual.R),Math.Max(Math.Abs(expected.G-actual.G),Math.Abs(expected.B-actual.B)))>2)mismatch++;
                }
            }
            if(phase==0)
                typeof(GlassButton).GetMethod("OnMouseEnter",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(close,new object[]{EventArgs.Empty});
            else if(phase==1)
                typeof(GlassButton).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(close,new object[]{new MouseEventArgs(MouseButtons.Left,1,22,22,0)});
            else {timer.Stop();form.Close();}
            phase++;
        };
        form.Shown+=(_,_)=>timer.Start();Application.Run(form);
        File.WriteAllText(Path.Combine(directory,"native-seam-check.json"),System.Text.Json.JsonSerializer.Serialize(new{mismatchedBoundaryPixels=mismatch,states=3,tolerance=2}));
        return mismatch==0?0:1;
    }
}

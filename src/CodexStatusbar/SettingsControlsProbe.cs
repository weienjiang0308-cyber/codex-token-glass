using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>Exercise the native context editor without committing global settings.</summary>
internal static class SettingsControlsProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var preferences=Path.Combine(Path.GetFullPath(directory),"isolated-ui.json");
        File.WriteAllText(preferences,"{\"language\":\"zh\",\"glassTransparency\":100}");
        Environment.SetEnvironmentVariable("CODEX_STATUSBAR_UI_SETTINGS",preferences);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        var config=CodexThemeSource.DefaultConfigPath;
        var before=SHA256.HashData(File.ReadAllBytes(config));
        using var form=new SettingsForm(()=>OverlayThemePalette.For(OverlayThemeKind.Light),()=>997500);
        foreach(var button in form.Controls.OfType<Button>())
            if(button.Text==CodexUi.T("保存更改","Save changes")||button.Text==CodexUi.T("恢复原值","Restore original"))button.Enabled=false;
        var editor=form.Controls.OfType<NumericUpDown>().Single(c=>c.Maximum>100);
        var overlaps=new List<string>();
        bool edited=false,stepped=false,linked=false,presets=false;
        using var timer=new System.Windows.Forms.Timer{Interval=900};var phase=0;
        timer.Tick+=(_,_)=>
        {
            if(phase++==0)
            {
                foreach(var control in form.Controls.Cast<Control>().Where(c=>c is NumericUpDown or ComboBox))
                foreach(var label in form.Controls.OfType<Label>())
                    if(control.Bounds.IntersectsWith(label.Bounds))overlaps.Add(control.AccessibleName+" / "+label.Text);
                editor.Text="1,000,000";
                editor.UpButton();edited=editor.Value==1_010_000;
                editor.DownButton();stepped=editor.Value==1_000_000;
                linked=form.ProbeContext.Value==1_000_000;
                form.ProbeContext.Value=500_000;form.FlushPendingInput();
                linked &= editor.Value==500_000;
                presets=true;
                foreach(var (caption,value) in new[]{("500K",500_000L),("1,000,000",1_000_000L),("1,050,000",1_050_000L)})
                {
                    form.Controls.OfType<Button>().Single(b=>b.Text==caption).PerformClick();
                    presets &= editor.Value==value && form.ProbeContext.Value==value;
                }
                editor.Value=1_000_000;
                return;
            }
            using var screenshot=new Bitmap(form.ClientSize.Width-24,230,PixelFormat.Format32bppRgb);
            using(var g=Graphics.FromImage(screenshot))g.CopyFromScreen(form.Left+12,form.Top+104,0,0,screenshot.Size);
            screenshot.Save(Path.Combine(directory,"context-controls.png"));
            using var full=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppRgb);
            using(var g=Graphics.FromImage(full))g.CopyFromScreen(form.PointToScreen(Point.Empty),Point.Empty,full.Size);
            full.Save(Path.Combine(directory,"settings-full.png"));
            timer.Stop();form.Close();
        };
        form.Shown+=(_,_)=>timer.Start();Application.Run(form);
        var unchanged=before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(config)));
        File.WriteAllText(Path.Combine(directory,"controls-check.json"),JsonSerializer.Serialize(new{overlaps,edited,stepped,linked,presets,configurationUnchanged=unchanged},new JsonSerializerOptions{WriteIndented=true}));
        return overlaps.Count==0&&edited&&stepped&&linked&&presets&&unchanged?0:1;
    }
}

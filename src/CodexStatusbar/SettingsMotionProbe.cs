using System.Diagnostics;
using System.Text.Json;

namespace CodexStatusbar;
internal static class SettingsMotionProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var prefs=Path.Combine(Path.GetFullPath(directory),"isolated-ui.json");
        File.WriteAllText(prefs,"{\"language\":\"zh\",\"glassTransparency\":75}");
        Environment.SetEnvironmentVariable("CODEX_STATUSBAR_UI_SETTINGS",prefs);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new SettingsForm(()=>OverlayThemePalette.For(OverlayThemeKind.Light),()=>997500);
        // Never allow benchmark gestures to save context configuration.
        foreach(Control c in form.Controls)if(c is Button)c.Enabled=false;
        form.Text="Slider performance verification";
        using var timer=new System.Windows.Forms.Timer{Interval=8};
        var clock=new Stopwatch();int phase=-1,inputs=0;ContextSlider? slider=null;
        var results=new List<object>();
        static double P(List<double> values,double q){var s=values.OrderBy(x=>x).ToArray();return s.Length==0?0:s[(int)Math.Clamp(Math.Ceiling(q*s.Length)-1,0,s.Length-1)];}
        void Finish(string name)
        {
            slider?.ProbeUp(22);form.MotionProbeActive=false;
            var gaps=form.ProbeFrameTimes.Zip(form.ProbeFrameTimes.Skip(1),(a,b)=>b-a).ToList();
            results.Add(new{name,inputs,frames=form.ProbeRenderMs.Count,renderMedianMs=P(form.ProbeRenderMs,.5),renderP95Ms=P(form.ProbeRenderMs,.95),frameGapP95Ms=P(gaps,.95),slowFrames=form.ProbeRenderMs.Count(x=>x>16.7)});
        }
        form.Shown+=(_,_)=>{clock.Start();timer.Start();};
        timer.Tick+=(_,_)=>
        {
            var time=clock.Elapsed.TotalSeconds;if(time<.6)return;
            var next=(int)((time-.6)/3);
            if(next!=phase)
            {
                if(phase>=0)Finish(phase==0?"context":"clarity");
                if(next>=2)
                {
                    timer.Stop();File.WriteAllText(Path.Combine(directory,"motion.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
                    form.Close();return;
                }
                phase=next;slider=phase==0?form.ProbeContext:form.ProbeClarity;inputs=0;
                form.ProbeRenderMs.Clear();form.ProbeFrameTimes.Clear();form.MotionProbeActive=true;slider.ProbeDown(22);
            }
            var t=((time-.6)%3)/3;var fraction=(1-Math.Cos(2*Math.PI*t))/2;
            slider!.ProbeMove((int)(22+(slider.Width-44)*fraction));inputs++;
        };
        Application.Run(form);
        File.WriteAllText(Path.Combine(directory,"fonts.json"),JsonSerializer.Serialize(new{body=CodexUi.FontName,display=CodexUi.DisplayFontName,chinese=CodexUi.ChineseFontName}));
        Console.WriteLine(File.ReadAllText(Path.Combine(directory,"motion.json")));return 0;
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Codex.SpeedMetrics;

namespace CodexStatusbar;

/// <summary>One alpha-composited glass surface; no native grid chrome or jagged Region clip.
/// Navigation is glass; data sits on a quieter reading surface.</summary>
internal sealed class StatisticsForm : Form
{
    private const int CanvasWidth=1000, CanvasHeight=760;
    private readonly UsageStatisticsService _service;
    private readonly Func<TokenSnapshot?> _selection;
    private readonly Func<OverlayThemePalette> _theme;
    private OverlayThemePalette _palette;
    private readonly LiquidBackdropRenderer _optics=new();
    private readonly UiFrameClock _animation=new();
    private readonly System.Windows.Forms.Timer _dataTimer=new(){Interval=1000};
    private readonly List<ActionButton> _buttons=new();
    private StatisticsSnapshot? _state;
    private TokenSnapshot? _selected;
    private IReadOnlyList<DailyUsage> _daily=Array.Empty<DailyUsage>();
    private UsageTotals _thread=UsageTotals.Sum(Array.Empty<UsageEntry>());
    private readonly List<string[]> _rows=new();
    private string[] _headings=Array.Empty<string>();
    private int _tab,_hover=-1,_pressed=-1,_focus=-1,_scroll;
    private bool _definitions,_painting;
    private string? _notice;
    private float _scale=1;
    private int _transparency=-1;
    private sealed record ActionButton(string Text,Rectangle Bounds,Action Action);
    private static readonly Rectangle Sheet=new(24,300,952,326);
    private static readonly Rectangle TableViewport=new(40,346,920,264);

    public StatisticsForm(UsageStatisticsService service,Func<TokenSnapshot?> selection,Func<OverlayThemePalette> theme)
    {
        _service=service;_selection=selection;_theme=theme;_palette=theme();
        _optics.PanelRadiusDip=48;
        Text="Statistics · 用量统计";FormBorderStyle=FormBorderStyle.None;
        AutoScaleMode=AutoScaleMode.None;ShowInTaskbar=true;TopMost=true;KeyPreview=true;
        StartPosition=FormStartPosition.Manual;BackColor=_palette.Background;
        var area=Screen.FromPoint(Cursor.Position).WorkingArea;
        _scale=Math.Min(DeviceDpi/96f,Math.Min((area.Width-48)/(float)CanvasWidth,(area.Height-48)/(float)CanvasHeight));
        ClientSize=new Size((int)(CanvasWidth*_scale),(int)(CanvasHeight*_scale));
        Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        _buttons.Add(new("当前任务",new Rectangle(32,242,122,44),()=>SelectTab(0)));
        _buttons.Add(new("每日记录",new Rectangle(156,242,122,44),()=>SelectTab(1)));
        _buttons.Add(new("模型明细",new Rectangle(280,242,122,44),()=>SelectTab(2)));
        _buttons.Add(new("统计口径",new Rectangle(32,684,120,44),()=>{_definitions=!_definitions;_scroll=0;RequestFrame();}));
        _buttons.Add(new("导出 JSON",new Rectangle(660,684,132,44),Export));
        _buttons.Add(new("刷新统计",new Rectangle(804,684,164,44),()=>{_service.RequestRefresh();_notice="已请求后台刷新";RequestFrame();}));
        _buttons.Add(new("关闭统计",new Rectangle(928,24,48,48),Close));
        _animation.Tick+=(_,_)=>{PaintFrame();if(!_optics.NeedsFrame)_animation.Stop();};
        _dataTimer.Tick+=(_,_)=>RefreshData();RefreshData();
    }
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x00080000;return p;}}
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _optics.Capture(new Rectangle(Location,ClientSize),new Rectangle(0,0,Width,Height),(uint)Math.Round(_scale*96));
        _dataTimer.Start();
    }
    protected override void OnShown(EventArgs e){base.OnShown(e);RequestFrame();}
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void OnPaint(PaintEventArgs e){PaintFrame();}
    private void RefreshData()
    {
        var state=_service.Current;var selected=_selection();var theme=_theme();
        if(ReferenceEquals(state,_state)&&selected?.ThreadId==_selected?.ThreadId&&selected?.TurnId==_selected?.TurnId&&theme==_palette&&_transparency==CodexUi.GlassTransparency)return;
        _transparency=CodexUi.GlassTransparency;
        _state=state;_selected=selected;_palette=theme;
        var today=DateOnly.FromDateTime(DateTime.UtcNow);
        _daily=state is null?Array.Empty<DailyUsage>():UsageMerge.Combine(state.OfficialDays,state.Entries,today);
        _thread=UsageTotals.Sum(state?.Entries.Where(x=>x.Thread==selected?.ThreadId)??Array.Empty<UsageEntry>());
        BuildRows();RequestFrame();
    }
    private void SelectTab(int tab){_tab=tab;_definitions=false;_scroll=0;BuildRows();RequestFrame();}
    private static string N(long n)=>n.ToString("N0",CultureInfo.InvariantCulture);
    private static string Short(long n)=>n>=1_000_000_000?(n/1_000_000_000d).ToString("0.00")+"B"
        :n>=1_000_000?(n/1_000_000d).ToString("0.00")+"M":n>=1000?(n/1000d).ToString("0.0")+"K":N(n);
    internal static string Price(UsageTotals t)=>t.Responses==0?"—":t.UnpricedResponses==t.Responses?"价格未知"
        :"$"+t.KnownApiUsd.ToString(t.KnownApiUsd is >0 and <.01m?"0.0000":"0.00",CultureInfo.InvariantCulture)+(t.UnpricedResponses>0?" *":"");
    private static string Rate(UsageTotals t)=>t.CachePercent is double p?p.ToString("0.0",CultureInfo.InvariantCulture)+"%":"—";
    private static string Coverage(UsageTotals t)=>$"{t.Responses-t.UnpricedResponses}/{t.Responses}";
    private void BuildRows()
    {
        _rows.Clear();var entries=_state?.Entries??Array.Empty<UsageEntry>();
        var thread=entries.Where(x=>x.Thread==_selected?.ThreadId).ToArray();var today=DateOnly.FromDateTime(DateTime.UtcNow);
        if(_tab==0)
        {
            _headings=["范围","总 Token","输入","缓存输入","输出","缓存率","API 等值","计价请求"];
            void Row(string name,IEnumerable<UsageEntry> items){var t=UsageTotals.Sum(items);_rows.Add([name,N(t.Total),N(t.Input),N(t.Cached),N(t.Output),Rate(t),Price(t),Coverage(t)]);}
            Row("本轮请求",thread.Where(x=>_selected?.TurnId is not null&&x.Turn==_selected.TurnId));Row("整个对话",thread);
            Row("今天 · 本机",entries.Where(x=>DateOnly.FromDateTime(x.At.UtcDateTime)==today));
            Row("本周 · 本机",entries.Where(x=>DateOnly.FromDateTime(x.At.UtcDateTime)>=UsageMerge.Monday(today)));
            Row("本月 · 本机",entries.Where(x=>DateOnly.FromDateTime(x.At.UtcDateTime)>=new DateOnly(today.Year,today.Month,1)));
        }
        else if(_tab==1)
        {
            _headings=["日期 · UTC 对齐","Token","数据来源"];
            foreach(var d in _daily.OrderByDescending(x=>x.Date))_rows.Add([d.Date.ToString("yyyy-MM-dd"),N(d.Tokens),d.Source]);
        }
        else
        {
            _headings=["本对话模型","Token","缓存率","API 等值 · USD","计价请求","价格核对"];
            foreach(var group in thread.GroupBy(x=>x.Model))
            {
                var t=UsageTotals.Sum(group);
                var stamp=ApiEquivalent.Prices.TryGetValue(group.Key,out var p)
                    ? (p.BuiltIn?"备用 ":"")+p.CheckedAt.ToLocalTime().ToString("MM-dd HH:mm") : "未知";
                _rows.Add([group.Key,N(t.Total),Rate(t),Price(t),Coverage(t),stamp]);
            }
        }
        _scroll=Math.Clamp(_scroll,0,Math.Max(0,_rows.Count-5));
    }
    private Rectangle Physical(Rectangle r)=>new((int)Math.Round(r.X*_scale),(int)Math.Round(r.Y*_scale),(int)Math.Round(r.Width*_scale),(int)Math.Round(r.Height*_scale));
    private void RequestFrame(){if(!IsHandleCreated||!Visible)return;PaintFrame();if(_optics.NeedsFrame)_animation.Start();}
    private void PaintFrame()
    {
        if(_painting||IsDisposed||!IsHandleCreated||Width<1||Height<1)return;_painting=true;
        try{using var bitmap=DrawFrame();LayeredSurface.Present(this,bitmap);}finally{_painting=false;}
    }
    internal Bitmap DrawFrame()
    {
        var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);bitmap.SetResolution(96,96);
        _optics.ReadingSurfaces((Physical(Sheet),20*_scale,Color.FromArgb(GlassAppearance.ReadingPlateAlpha(CodexUi.GlassTransparency),_palette.Background)));
        for(int i=0;i<_buttons.Count;i++)_optics.Control(i,Physical(_buttons[i].Bounds),_buttons[i].Bounds.Height*_scale/2,_hover==i,_pressed==i,i==_tab&&!_definitions);
        var optical=_optics.RenderInto(bitmap,_palette.Background.GetBrightness()<.5);
        using var g=Graphics.FromImage(bitmap);g.ScaleTransform(_scale,_scale);g.SmoothingMode=SmoothingMode.AntiAlias;
        g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        if(!optical){Fill(g,new Rectangle(0,0,CanvasWidth,CanvasHeight),48,_palette.Background);Fill(g,Sheet,20,CodexUi.Mix(_palette.Background,_palette.Value,.035));}
        TextAt(g,"用量统计",32,28,700,38,25,true);TextAt(g,"Statistics  /  当前对话与账户用量",33,72,760,24,13,false,true);
        var today=DateOnly.FromDateTime(DateTime.UtcNow);long Period(DateOnly start)=>_daily.Where(x=>x.Date>=start).Sum(x=>x.Tokens);
        Hero(g,32,"本对话 · API 等值（USD）",Price(_thread),$"Standard  ·  已计价 {Coverage(_thread)} 个请求");
        Hero(g,370,"本周 Token · 暂计",Short(Period(UsageMerge.Monday(today))),$"{UsageMerge.Monday(today):MM-dd} 周一至今");
        Hero(g,685,"本月 Token · 暂计",Short(Period(new DateOnly(today.Year,today.Month,1))),$"{today:MM} 月 01 日至今");
        using(var line=new Pen(Color.FromArgb(25,_palette.Value),1)){g.DrawLine(line,350,126,350,207);g.DrawLine(line,663,126,663,207);}
        for(int i=0;i<_buttons.Count;i++)DrawButton(g,i,optical);
        if(_definitions)DrawDefinitions(g);else DrawTable(g);
        var last=_state?.OfficialDays.Count>0?_state.OfficialDays.Keys.Max().ToString("MM-dd"):"未连接";
        var local=_state?.LocalFetched.ToLocalTime().ToString("HH:mm:ss")??"读取中";
        TextAt(g,_state?.Error??_notice??$"官方历史截至 {last}   ·   本机更新 {local}   ·   当日数据暂计",33,638,934,22,12,false,true);
        TextAt(g,"Standard API 等值，非账单 · "+(_state?.PriceStatus??"官方价格待核对")+" · UTC 暂估 · * 部分计价",33,661,934,19,11,false,true);
        return bitmap;
    }
    internal Bitmap Preview(float scale, int tab = 0)
    {
        _scale = scale; ClientSize = new Size((int)(CanvasWidth*scale),(int)(CanvasHeight*scale));
        using var backdrop = new Bitmap(Width,Height,PixelFormat.Format32bppRgb);
        using(var g = Graphics.FromImage(backdrop))
        {
            var dark = _palette.Background.GetBrightness()<.5;
            g.Clear(dark ? Color.FromArgb(35,39,43) : Color.FromArgb(232,235,233));
            using var brush = new LinearGradientBrush(new Rectangle(0,0,Width,Height),
                dark ? Color.FromArgb(45,55,70) : Color.FromArgb(200,217,229),
                dark ? Color.FromArgb(50,58,52) : Color.FromArgb(225,231,216),30);
            g.FillRectangle(brush,new Rectangle(0,0,Width,Height));
        }
        _optics.SetBackdrop(backdrop,new Rectangle(0,0,Width,Height),(uint)Math.Round(scale*96),false);
        _tab=tab;BuildRows();return DrawFrame();
    }
    private void Hero(Graphics g,int x,string label,string value,string detail)
    {TextAt(g,label,x,118,290,23,13,false,true);TextAt(g,value,x,147,292,48,value.Length>12?30:38,true);TextAt(g,detail,x,201,292,20,12,false,true);}
    private void DrawButton(Graphics g,int i,bool optical)
    {
        var b=_buttons[i];bool selected=i==_tab&&!_definitions;
        if(!optical)Fill(g,b.Bounds,b.Bounds.Height/2,selected?_palette.Value:CodexUi.Mix(_palette.Background,_palette.Value,_hover==i?.09:.045));
        var color=selected?_palette.Background:_palette.Value;
        if(i==6){using var p=new Pen(color,1.8f){StartCap=LineCap.Round,EndCap=LineCap.Round};var x=b.Bounds.Left+24;var y=b.Bounds.Top+24;g.DrawLine(p,x-6,y-6,x+6,y+6);g.DrawLine(p,x+6,y-6,x-6,y+6);}
        else TextAt(g,i==3&&_definitions?"返回明细":b.Text,b.Bounds.X,b.Bounds.Y,b.Bounds.Width,b.Bounds.Height,13,selected,false,StringAlignment.Center,color);
        if(_focus==i){using var path=LayeredSurface.Round(RectangleF.Inflate(b.Bounds,-3,-3),b.Bounds.Height/2f-3);using var pen=new Pen(color,1.5f){DashStyle=DashStyle.Dot};g.DrawPath(pen,path);}
    }
    private void DrawTable(Graphics g)
    {
        var weights=_tab switch{0=>new[]{1.05,1.12,1.12,1.12,.84,.64,.80,.74},1=>new[]{1.0,1.1,1.4},_=>new[]{1.5,1.15,.65,1.0,.65,1.0}};
        var sum=weights.Sum();var x=40f;
        for(int c=0;c<_headings.Length;c++){var w=(float)(920*weights[c]/sum);TextAt(g,_headings[c],x+8,310,w-16,32,11.5f,true,true);x+=w;}
        using(var line=new Pen(Color.FromArgb(22,_palette.Value),1))g.DrawLine(line,48,344,952,344);
        var saved=g.Save();g.SetClip(TableViewport);
        if(_state is null)TextAt(g,"正在后台读取用量…",48,375,880,40,15,false,true);
        else if(_rows.Count==0)TextAt(g,"此范围尚无可用记录",48,375,880,40,15,false,true);
        for(int r=_scroll;r<Math.Min(_rows.Count,_scroll+6);r++)
        {
            var y=347+(r-_scroll)*50;if(r==1&&_tab==0)Fill(g,new Rectangle(40,y+3,920,44),12,Color.FromArgb(20,_palette.Value));x=40;
            for(int c=0;c<_headings.Length;c++){var w=(float)(920*weights[c]/sum);TextAt(g,_rows[r][c],x+8,y,w-16,50,11.5f,c==6&&_tab==0);x+=w;}
            if(r+1<_rows.Count)using(var line=new Pen(Color.FromArgb(13,_palette.Value),1))g.DrawLine(line,48,y+49,952,y+49);
        }
        g.Restore(saved);
        if(_rows.Count>5){var h=Math.Max(24,252*5/_rows.Count);var y=352+(252-h)*_scroll/Math.Max(1,_rows.Count-5);Fill(g,new Rectangle(967,y,3,h),2,Color.FromArgb(100,_palette.Value));}
    }
    private string Definitions=>"统计范围\n本轮为当前这一轮请求；整个对话不含独立子对话。本机统计不含其他设备和已删除日志。\n\n"
        +"历史与今天\n官方历史日桶替换同日本机数，不相加。未返回的日期用本机暂计；官方没有时区字段，暂按 UTC 对齐。\n\n"
        +"Token 与费用\n总量 = 输入 + 输出。缓存是输入子集，推理是输出子集。按记录中的模型和最新核对的官方 Standard 单价逐请求折算，含长上下文规则；非套餐账单。价格每小时核对，新模型立即查询；失败用注明时间的缓存，未知模型不猜价。\n\n"
        +$"当前会话 ID  {_selected?.ThreadId??"未选择"}\n读取异常 {_state?.ReadErrors??0} · 旧格式文件 {_state?.LegacyFiles??0}（旧格式可能不完整）";
    private void DrawDefinitions(Graphics g)
    {using var font=CodexUi.FontForText("中文",10);using var brush=new SolidBrush(_palette.Value);g.DrawString(Definitions,font,brush,new RectangleF(48,318,900,300));}
    private void TextAt(Graphics g,string text,float x,float y,float w,float h,float size,bool bold=false,bool muted=false,StringAlignment align=StringAlignment.Near,Color? color=null)
    {
        using var font=CodexUi.FontForText(text,size*72/96,bold);using var brush=new SolidBrush(color??(muted?CodexUi.Mix(_palette.Background,_palette.Value,.82):_palette.Value));
        using var format=new StringFormat{Alignment=align,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter};
        if(!AppleText.Draw(g,text,font,new RectangleF(x,y,w,h),brush.Color,align))g.DrawString(text,font,brush,new RectangleF(x,y,w,h),format);
    }
    private static void Fill(Graphics g,Rectangle r,float radius,Color color){using var p=LayeredSurface.Round(r,radius);using var b=new SolidBrush(color);g.FillPath(b,p);}
    private int Hit(Point p)=>_buttons.FindIndex(b=>Physical(b.Bounds).Contains(p));
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);var next=Hit(e.Location);if(next!=_hover){_hover=next;Cursor=next>=0?Cursors.Hand:Cursors.Default;RequestFrame();}}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);_hover=-1;RequestFrame();}
    protected override void OnMouseDown(MouseEventArgs e)
    {base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;var hit=Hit(e.Location);if(hit>=0){_pressed=hit;_focus=hit;Capture=true;RequestFrame();}else if(e.Y<106*_scale){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}}
    protected override void OnMouseUp(MouseEventArgs e)
    {base.OnMouseUp(e);var hit=Hit(e.Location);var pressed=_pressed;_pressed=-1;Capture=false;if(e.Button==MouseButtons.Left&&hit>=0&&hit==pressed)_buttons[hit].Action();RequestFrame();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture&&_pressed>=0){_pressed=-1;RequestFrame();}}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);_scroll=Math.Clamp(_scroll-Math.Sign(e.Delta)*3,0,Math.Max(0,_rows.Count-5));RequestFrame();}
    protected override bool ProcessCmdKey(ref Message msg,Keys keys)
    {
        if(keys==Keys.Escape){if(_definitions){_definitions=false;RequestFrame();}else Close();return true;}
        if(keys is Keys.Tab or (Keys.Shift|Keys.Tab)){_focus=(_focus+(keys==Keys.Tab?1:_buttons.Count-1)+_buttons.Count)%_buttons.Count;RequestFrame();return true;}
        if((keys is Keys.Enter or Keys.Space)&&_focus>=0){_buttons[_focus].Action();return true;}
        if(keys is Keys.PageDown or Keys.PageUp){_scroll=Math.Clamp(_scroll+(keys==Keys.PageDown?5:-5),0,Math.Max(0,_rows.Count-5));RequestFrame();return true;}
        if((keys is Keys.Left or Keys.Right)&&_focus is >=0 and <=2){_focus=(_focus+(keys==Keys.Right?1:2))%3;SelectTab(_focus);return true;}
        return base.ProcessCmdKey(ref msg,keys);
    }
    private void Export()
    {
        if(_state is null)return;using var dialog=new SaveFileDialog{Filter="JSON|*.json",FileName="codex-statistics-"+DateTime.Now.ToString("yyyyMMdd")+".json"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new{threadId=_selected?.ThreadId,turnId=_selected?.TurnId,definitions=Definitions,priceSource=ApiEquivalent.Source,prices=ApiEquivalent.Prices,snapshot=_state},new JsonSerializerOptions{WriteIndented=true}));_notice="JSON 已导出";}
        catch(IOException){_notice="文件无法写入，请选择其他位置";}RequestFrame();
    }
    protected override AccessibleObject CreateAccessibilityInstance()=>new StatisticsAccessible(this);
    private sealed class StatisticsAccessible(StatisticsForm form):ControlAccessibleObject(form)
    {
        public override string? Name{get=>"用量统计";set{}}
        public override string? Value{get=>string.Join("；",form._rows.Select(r=>string.Join("，",r)));set{}}
        public override int GetChildCount()=>form._buttons.Count;
        public override AccessibleObject GetChild(int index)=>new ActionAccessible(form,index);
    }
    private sealed class ActionAccessible(StatisticsForm form,int index):AccessibleObject
    {
        public override string? Name{get=>form._buttons[index].Text;set{}}
        public override AccessibleRole Role=>index<3?AccessibleRole.PageTab:AccessibleRole.PushButton;
        public override Rectangle Bounds=>form.RectangleToScreen(form.Physical(form._buttons[index].Bounds));
        public override AccessibleStates State=>AccessibleStates.Focusable|(form._focus==index?AccessibleStates.Focused:0)|(index==form._tab?AccessibleStates.Selected:0);
        public override string DefaultAction=>"打开";
        public override void DoDefaultAction()=>form.BeginInvoke(form._buttons[index].Action);
    }
    protected override void Dispose(bool disposing){if(disposing){_animation.Dispose();_dataTimer.Dispose();_optics.Dispose();}base.Dispose(disposing);}
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
}

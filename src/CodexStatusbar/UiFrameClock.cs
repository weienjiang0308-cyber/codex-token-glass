using System.Runtime.InteropServices;

namespace CodexStatusbar;
internal static class GlassMotion
{
    public const int FrameMilliseconds=16;
    public const double PressScale=1.07;
    public const double EntrySeconds=.24;
    public const double FeedbackSeconds=1.2;
    public const int PositionSettleMilliseconds=100;
}

/// <summary>One animation cadence across the UI. Request fine Windows timer resolution only while
/// visible animation is running; balance every request when idle or disposed.</summary>
internal sealed class UiFrameClock : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private SynchronizationContext? _context;
    private volatile bool _active;
    private int _posted;
    private long _deadline;
    private bool _fineResolution;
    private bool _disposed;
    public event EventHandler? Tick;
    public UiFrameClock(){_timer=new System.Threading.Timer(Pulse,null,Timeout.Infinite,Timeout.Infinite);}
    public void Start()
    {
        if(_active||_disposed)return;
        _context??=SynchronizationContext.Current??new WindowsFormsSynchronizationContext();
        _fineResolution=timeBeginPeriod(1)==0;_active=true;_deadline=0;_timer.Change(0,4);
    }
    private void Pulse(object? state)
    {
        if(!_active||System.Diagnostics.Stopwatch.GetTimestamp()<Interlocked.Read(ref _deadline)||Interlocked.CompareExchange(ref _posted,1,0)!=0)return;
        try{_context!.Post(_=>
        {
            try
            {
                if(!_active)return;
                Interlocked.Exchange(ref _deadline,System.Diagnostics.Stopwatch.GetTimestamp()+
                    System.Diagnostics.Stopwatch.Frequency*GlassMotion.FrameMilliseconds/1000);
                Tick?.Invoke(this,EventArgs.Empty);
            }
            finally{Interlocked.Exchange(ref _posted,0);}
        },null);}
        catch(Exception error)when(error is ObjectDisposedException or System.ComponentModel.InvalidAsynchronousStateException)
        {Interlocked.Exchange(ref _posted,0);}
    }
    public void Stop(){if(_disposed)return;_active=false;_timer.Change(Timeout.Infinite,Timeout.Infinite);if(_fineResolution){timeEndPeriod(1);_fineResolution=false;}}
    public void Dispose(){if(_disposed)return;Stop();_disposed=true;_timer.Dispose();}
    [DllImport("winmm.dll")]private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")]private static extern uint timeEndPeriod(uint ms);
}

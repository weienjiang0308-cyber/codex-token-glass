using System.Windows.Automation;

namespace CodexStatusbar;

internal sealed record VisibleConversation(string? ThreadId, string Title, string Source);
internal sealed record VisibleRouteSample(VisibleConversation Route, long Window, long Version, DateTimeOffset DetectedAt);

/// <summary>Independent lightweight routing; large rollout reads cannot delay page detection.</summary>
internal sealed class VisibleConversationMonitor : IDisposable
{
    private readonly Func<long> _window;
    private readonly string _database;
    private CancellationTokenSource? _stop;
    private long _version;
    private VisibleRouteSample _latest=new(new(null,"","visible-unresolved"),0,0,DateTimeOffset.UtcNow);
    public VisibleRouteSample Latest=>Volatile.Read(ref _latest);
    public VisibleConversationMonitor(Func<long> window,string database){_window=window;_database=database;}
    public void Start()
    {
        if(_stop is not null)return;
        var stop=_stop=new CancellationTokenSource();
        _=Task.Run(async()=>
        {
            var resolver=new VisibleConversationResolver();
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    var window=_window();var route=resolver.Read(window,_database);var previous=Latest;
                    if(!stop.IsCancellationRequested && (previous.Window!=window||previous.Route!=route))
                        Volatile.Write(ref _latest,new(route,window,Interlocked.Increment(ref _version),DateTimeOffset.UtcNow));
                    await Task.Delay(150,stop.Token);
                }
            }
            catch(OperationCanceledException){}
        });
    }
    public void Stop(){_stop?.Cancel();_stop=null;}
    public void Dispose()=>Stop();
}

/// <summary>Resolve the page in the attached window, never the newest background subscription.</summary>
internal sealed class VisibleConversationResolver
{
    private AutomationElement? _document;
    private long _window;

    public VisibleConversation Read(long window, string databasePath)
    {
        var title = ReadTitle(window);
        var row = string.IsNullOrWhiteSpace(title) ? null : StateDatabase.TryReadUniqueDisplayName(databasePath, title);
        return new(row?.Id, title ?? "", row is null ? "visible-unresolved" : "visible-title");
    }

    public string? ReadTitle(long window)
    {
        if (window == 0) { _document = null; _window = 0; return null; }
        try
        {
            if (_window != window) { _document = null; _window = window; }
            _document ??= AutomationElement.FromHandle(new IntPtr(window))?.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")));
            return _document?.Current.Name?.Trim();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        { _document = null; return null; }
    }
}

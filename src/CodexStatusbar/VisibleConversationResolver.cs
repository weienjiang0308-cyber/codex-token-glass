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
    private readonly Func<string?>? _activeThread;
    public VisibleConversationMonitor(Func<long> window,string database,Func<string?>? activeThread=null){_window=window;_database=database;_activeThread=activeThread;}

    /// <summary>A display name shared by several chats cannot select one by itself. The IPC route
    /// already names the chat the window follows; accept it ONLY when its own display name is the
    /// visible title, so a background subscription can never be shown under another chat's title.</summary>
    internal static VisibleConversation Disambiguate(VisibleConversation route,string? activeThread,string database)
    {
        if(route.ThreadId is not null || string.IsNullOrWhiteSpace(route.Title) || string.IsNullOrWhiteSpace(activeThread))return route;
        var row=StateDatabase.TryReadDisplayThread(database,activeThread);
        return row is not null && string.Equals(row.Title?.Trim(),route.Title,StringComparison.Ordinal)
            ? new(row.Id,route.Title,"visible-title-ipc") : route;
    }
    public void Start()
    {
        if(_stop is not null)return;
        var stop=_stop=new CancellationTokenSource();
        _=Task.Run(async()=>
        {
            using var wake = new SemaphoreSlim(0, 1);
            using var resolver=new VisibleConversationResolver();
            resolver.TitleChanged = () => { try { if (wake.CurrentCount == 0) wake.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } };
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    var window=_window();var route=Disambiguate(resolver.Read(window,_database),_activeThread?.Invoke(),_database);var previous=Latest;
                    if(!stop.IsCancellationRequested && (previous.Window!=window||previous.Route!=route))
                        Volatile.Write(ref _latest,new(route,window,Interlocked.Increment(ref _version),DateTimeOffset.UtcNow));
                    await wake.WaitAsync(150,stop.Token);
                }
            }
            catch(OperationCanceledException){}
        });
    }
    public void Stop(){_stop?.Cancel();_stop=null;}
    public void Dispose()=>Stop();
}

/// <summary>Resolve the page in the attached window, never the newest background subscription.</summary>
internal sealed class VisibleConversationResolver : IDisposable
{
    private AutomationElement? _document;
    private long _window;
    private AutomationPropertyChangedEventHandler? _nameChanged;
    private string? _lastTitle;
    private VisibleConversation? _lastRoute;
    private long _lastQuery;
    public Action? TitleChanged { get; set; }

    public VisibleConversation Read(long window, string databasePath)
    {
        var title = ReadTitle(window);
        var now = Environment.TickCount64;
        if (title == _lastTitle && _lastRoute?.ThreadId is not null && now - _lastQuery < 500)
            return _lastRoute;
        var row = string.IsNullOrWhiteSpace(title) ? null : StateDatabase.TryReadUniqueDisplayName(databasePath, title);
        _lastTitle = title; _lastQuery = now;
        return _lastRoute = new(row?.Id, title ?? "", row is null ? "visible-unresolved" : "visible-title");
    }

    public string? ReadTitle(long window)
    {
        if (window == 0) { ResetDocument(); _window = 0; return null; }
        try
        {
            if (_window != window) { ResetDocument(); _window = window; }
            if (_document is null)
            {
                _document = AutomationElement.FromHandle(new IntPtr(window))?.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")));
                if (_document is not null)
                {
                    _nameChanged = (_, _) => TitleChanged?.Invoke();
                    try { Automation.AddAutomationPropertyChangedEventHandler(_document, TreeScope.Element,
                        _nameChanged, AutomationElement.NameProperty); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                    { _nameChanged = null; } // Polling remains available on providers without events.
                }
            }
            return _document?.Current.Name?.Trim();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        { ResetDocument(); return null; }
    }
    private void ResetDocument()
    {
        if (_document is not null && _nameChanged is not null)
            try { Automation.RemoveAutomationPropertyChangedEventHandler(_document, _nameChanged); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _document = null; _nameChanged = null; _lastRoute = null; _lastTitle = null;
    }
    public void Dispose() => ResetDocument();
}

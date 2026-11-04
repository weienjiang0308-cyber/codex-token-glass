using System.Windows.Automation;

namespace CodexStatusbar;

internal sealed record VisibleConversation(string? ThreadId, string Title, string Source);

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
            or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { _document = null; return null; }
    }
}

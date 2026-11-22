using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace CodexStatusbar;

/// <summary>Current-user-only control channel: shutdown for safe installation updates, plus UI
/// commands (open panel, settings, statistics) used for live visual verification.</summary>
internal sealed class LocalControl : IDisposable
{
    internal const byte ExitCommand = 1;
    internal const byte TogglePanelCommand = 2;
    internal const byte OpenSettingsCommand = 3;
    internal const byte CloseSettingsCommand = 4;
    internal const byte OpenStatisticsCommand = 5;
    internal const byte CloseStatisticsCommand = 6;
    private readonly CancellationTokenSource _stop=new();
    private NamedPipeServerStream? _pipe;
    private readonly string _name;
    private static string PipeName=>"CodexTokenGlass.Control."+(WindowsIdentity.GetCurrent().User?.Value??Environment.UserName);
    public LocalControl(Action quit,string? name=null,Action<byte>? command=null)
    {
        _name=name??PipeName;
        _=Task.Run(async()=>
        {
            while(!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe=new NamedPipeServerStream(_name,PipeDirection.In,1,PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    _pipe=pipe;
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var bytes=new byte[1];var count=await pipe.ReadAsync(bytes,_stop.Token);
                    if(count==1 && bytes[0]==ExitCommand)quit();
                    else if(count==1)command?.Invoke(bytes[0]);
                }
                catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
            }
        });
    }
    public static bool RequestExit(string? name=null)=>Send(ExitCommand,name);

    public static bool Send(byte value,string? name=null)
    {
        try
        {
            using var pipe=new NamedPipeClientStream(".",name??PipeName,PipeDirection.Out,PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);pipe.WriteByte(value);pipe.Flush();return true;
        }
        catch(Exception e)when(e is IOException or TimeoutException){return false;}
    }
    public void Dispose(){_stop.Cancel();_pipe?.Dispose();}
}

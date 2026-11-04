using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Diagnostics;
using Avalonia.Threading;

namespace CallOfAI;

static class Program
{
    public static Updates Service = null!;
    public static string? LocalFeed;
    static FileStream? instanceLock;
    public static string RunningVersion => typeof(Program).Assembly.GetName().Version!.ToString();
    public static ProcessStartInfo StartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(executable)! };
        if(LocalFeed!=null) { info.ArgumentList.Add("--local-test"); info.ArgumentList.Add(LocalFeed); }
        info.ArgumentList.Add("--wait-for-exit"); info.ArgumentList.Add(Environment.ProcessId.ToString());
        return info;
    }
    [STAThread]
    public static void Main(string[] args)
    {
        for(int i=0;i<args.Length;i++)
        {
            if(args[i]=="--local-test" && i+1<args.Length) LocalFeed=Path.GetFullPath(args[++i]);
            else if(args[i]=="--wait-for-exit" && i+1<args.Length)
            {
                int pid=int.Parse(args[++i]);
                try { using var parent=Process.GetProcessById(pid); if(!parent.WaitForExit(30000)) return; }
                catch(ArgumentException) { /* Parent has already exited. */ }
            }
            else throw new ArgumentException("Unknown launcher argument");
        }
        var test = LocalFeed!=null;
        var home = test ? Path.Combine(LocalFeed!,"installed") : Updates.DefaultHome;
        Service = new Updates(home, Updates.EmbeddedKey(),test);
        // Original downloaded bootstrap always forwards to the selected updated version.
        var current=Service.Current("launcher");
        if(current!=null && LauncherFlow.VersionOf(current.Version)>=LauncherFlow.VersionOf(RunningVersion))
        {
            var exe=Service.Executable("launcher",current);
            if(File.Exists(exe) && !Path.GetFullPath(exe).Equals(Environment.ProcessPath,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            { Process.Start(StartInfo(exe)); return; }
        }
        try { instanceLock=new FileStream(Path.Combine(home,"launcher.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { return; }
        try { AppBuilder.Configure<LauncherApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
        finally { instanceLock.Dispose(); }
    }
}

sealed class LauncherApp : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant=ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow=new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class MainWindow : Window
{
    readonly TextBlock status=new(){Text="Checking launcher version…",TextWrapping=TextWrapping.Wrap};
    readonly TextBlock version=new();
    readonly TextBlock notes=new(){TextWrapping=TextWrapping.Wrap,Text="No release checked yet."};
    readonly ProgressBar bar=new(){IsIndeterminate=false,Height=5};
    readonly Button primary=new(){Content="INSTALL",MinWidth=230,Height=58,FontSize=20,IsEnabled=false};
    readonly Button quit=new(){Content="QUIT",MinWidth=160,Height=58,FontSize=20};
    readonly Button repair=new(){Content="Repair",FontSize=12,Padding=new Thickness(14,5),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=false};
    readonly DispatcherTimer retry=new(){Interval=TimeSpan.FromSeconds(15)};
    readonly Updates service=Program.Service;
    CancellationTokenSource? cancellation;
    Process? game;
    Package? latestGame;
    GameAction action;
    bool closeRequested;
    bool restarting;
    public MainWindow()
    {
        Title="Call of AI — Launcher"; Width=850; Height=540; MinWidth=700; MinHeight=440;
        Background=Brush.Parse("#141C1D");
        var layout=new StackPanel{Margin=new Thickness(36),Spacing=20};
        layout.Children.Add(new TextBlock{Text="CALL OF AI",FontSize=46,FontWeight=FontWeight.Bold,Foreground=Brush.Parse("#D8BC83")});
        layout.Children.Add(new TextBlock{Text="DEPLOYMENT DESK   /   "+Updates.Platform+ (Program.LocalFeed!=null?"   /   LOCAL TEST":""),FontSize=14});
        layout.Children.Add(version);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16};
        actions.Children.Add(primary); actions.Children.Add(quit);
        layout.Children.Add(actions);layout.Children.Add(repair);layout.Children.Add(bar); layout.Children.Add(status);
        layout.Children.Add(new ScrollViewer{Content=notes,MaxHeight=140});
        Content=layout;
        Refresh();
        primary.Click+=async(_,_)=>await Run(PrimaryAction);
        repair.Click+=async(_,_)=>await Run(InstallGame);
        quit.Click+=(_,_)=>Close();
        Opened+=async (_,_)=>await Run(Startup);
        retry.Tick+=async(_,_)=>{retry.Stop();await Run(Startup);};
        Closing+=(_,e)=>
        {
            retry.Stop();closeRequested=true;
            if(cancellation!=null){e.Cancel=true;status.Text="Finishing safely…";cancellation.Cancel();}
        };
        Closed+=(_,_)=>retry.Stop();
    }
    async Task Run(Func<Task> work)
    {
        if(cancellation!=null || closeRequested)return;
        cancellation=new();primary.IsEnabled=false;repair.IsEnabled=false;bar.IsIndeterminate=true;
        try{await work();}
        catch(OperationCanceledException){status.Text="Cancelled. Your current installation is unchanged.";}
        catch(Exception e)
        {
            status.Text=e.Message+(latestGame==null?" — Retrying version checks in 15 seconds.":" — You can try again.");
            if(latestGame==null && !closeRequested)retry.Start();
        }
        finally
        {
            cancellation.Dispose();cancellation=null;bar.IsIndeterminate=false;
            if(closeRequested || restarting)Close();else Refresh();
        }
    }
    async Task<Package> Check(string component)
    {
        status.Text=$"Checking {component} version…";
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await service.Check(component,timeout.Token,Program.LocalFeed==null?null:new Uri(Path.Combine(Program.LocalFeed,component+"-"+Updates.Platform+".json")).AbsoluteUri); }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested) { throw new IOException("Version check timed out"); }
    }
    void EnsureStopped(){if(game!=null&&!game.HasExited)throw new InvalidOperationException("Close the game before updating or changing versions.");}
    async Task Startup()
    {
        retry.Stop();latestGame=null;
        latestGame=await LauncherFlow.Startup(Program.RunningVersion,Check,async package=>
        {
            status.Text="Updating launcher automatically…";
            var selected=service.Current("launcher");
            if(selected!=null && LauncherFlow.VersionOf(selected.Version)>=LauncherFlow.VersionOf(package.Version) &&
                service.Executable("launcher",selected).Equals(Environment.ProcessPath,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
                throw new InvalidDataException("Launcher package version does not match its manifest. Publisher must correct the release.");
            await service.Install("launcher",package,Progress(),cancellation!.Token);
        },()=>
        {
            cancellation!.Token.ThrowIfCancellationRequested();
            var selected=service.Current("launcher")!;
            using var child=Process.Start(Program.StartInfo(service.Executable("launcher",selected))) ?? throw new IOException("Could not restart updated launcher");
            restarting=true;status.Text="Restarting updated launcher…";
        });
        if(latestGame!=null){notes.Text=latestGame.Notes;status.Text="Version checks complete.";}
    }
    IProgress<string> Progress()=>new Progress<string>(s=>{if(cancellation!=null && !closeRequested && !restarting)status.Text=s;});
    async Task InstallGame()
    {
        EnsureStopped();var package=await Check("game");latestGame=package;notes.Text=package.Notes;
        await service.Install("game",package,Progress(),cancellation!.Token);
        status.Text="Game ready. Press PLAY.";
    }
    async Task PrimaryAction()
    {
        EnsureStopped();
        if(action!=GameAction.Play){await InstallGame();return;}
        game?.Dispose();game=service.Play();status.Text="Game running.";
        _=ObserveGame(game);
    }
    async Task ObserveGame(Process launched)
    {
        await launched.WaitForExitAsync();
        if(!closeRequested)Dispatcher.UIThread.Post(()=>{if(!closeRequested && cancellation==null){status.Text="Game closed. Ready to play.";Refresh();}});
    }
    void Refresh()
    {
        try
        {
            var installed=service.Current("game");
            version.Text=$"Game: {installed?.Version??"not installed"}     Launcher: {Program.RunningVersion}";
            if(latestGame==null){primary.IsEnabled=false;repair.IsEnabled=false;return;}
            action=LauncherFlow.Action(installed,installed!=null && File.Exists(service.Executable("game",installed)),latestGame);
            primary.Content=action.ToString().ToUpperInvariant();
            bool running=game!=null && !game.HasExited;
            primary.IsEnabled=!running;repair.IsEnabled=!running;
        }
        catch(Exception e){primary.IsEnabled=false;repair.IsEnabled=false;status.Text="Cannot read installation: "+e.Message;}
    }
}
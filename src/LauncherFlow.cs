namespace CallOfAI;

public enum GameAction { Install, Update, Play }
public static class LauncherFlow
{
    public static Version VersionOf(string value)
    {
        var v = Version.Parse(value);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
    public static GameAction Action(Installation? installed, bool executableExists, Package latest)
    {
        if (installed == null || !executableExists) return GameAction.Install;
        return VersionOf(installed.Version) < VersionOf(latest.Version) ? GameAction.Update : GameAction.Play;
    }
    // A null result means restart was requested: do not check the game in this process.
    public static async Task<Package?> Startup(string runningVersion,
        Func<string, Task<Package>> check, Func<Package, Task> installLauncher, Action restart)
    {
        var launcher = await check("launcher");
        if (VersionOf(launcher.Version) > VersionOf(runningVersion))
        {
            await installLauncher(launcher);
            restart();
            return null;
        }
        return await check("game");
    }
}
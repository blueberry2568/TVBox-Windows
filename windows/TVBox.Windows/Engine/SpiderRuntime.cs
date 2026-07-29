namespace TVBoxForWindows.Engine;

/// <summary>爬虫运行时（Windows 版仅保留 Node.js，已移除 JAR/Python 支持）。</summary>
public static class SpiderRuntime
{
    /// <summary>关闭所有运行时（App 退出/配置切换时调用；不留孤儿 node 进程）。</summary>
    public static void Shutdown()
    {
        try { NodeRuntime.Shutdown(); } catch { }
    }

    public static void TerminateForExit()
    {
        try { NodeRuntime.TerminateForExit(); } catch { }
    }
}

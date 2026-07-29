namespace TVBoxForWindows.UI;

/// <summary>详情页导航参数（契约 §5.5）：常规为 SiteKey+VodId，推送播放走 PushUrl。</summary>
public class DetailArgs
{
    public string SiteKey;
    public string VodId;
    public string Name;
    public string PushUrl;
}

/// <summary>播放页导航参数（契约 §5.5）。</summary>
public class PlayerArgs
{
    public PlaySession Session;
}

/// <summary>直播页导航参数：可指定初始分组/频道名（均可空 = 默认）。</summary>
public class LiveArgs
{
    public string GroupName;
    public string ChannelName;
}

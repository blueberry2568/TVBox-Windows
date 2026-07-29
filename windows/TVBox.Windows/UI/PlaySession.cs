using TVBoxForWindows.Core;

namespace TVBoxForWindows.UI;

/// <summary>播放会话（页面间传参与换源状态机，对应 Android VideoActivity 的线路/集数/历史状态）。</summary>
public class PlaySession
{
    public Models.Site Site;
    public Models.Vod Vod;
    public List<Models.VodFlag> Flags = new();
    public int FlagIndex;
    public int EpisodeIndex;
    /// <summary>进度/倍速/片头尾等（来自 Stores，不存在则新建）。</summary>
    public Models.History History;

    public Models.VodFlag CurrentFlag =>
        Flags is { Count: > 0 } ? Flags[Math.Clamp(FlagIndex, 0, Flags.Count - 1)] : null;

    public Models.Episode CurrentEpisode
    {
        get
        {
            var episodes = CurrentFlag?.Episodes;
            return episodes is { Count: > 0 } ? episodes[Math.Clamp(EpisodeIndex, 0, episodes.Count - 1)] : null;
        }
    }

    /// <summary>从详情页创建会话：解析线路集数并挂接历史记录。</summary>
    public static PlaySession FromDetail(Models.Site site, Models.Vod vod, int flag, int ep)
    {
        var session = new PlaySession { Site = site, Vod = vod, Flags = vod?.GetFlags() ?? new List<Models.VodFlag>() };
        session.FlagIndex = session.Flags.Count > 0 ? Math.Clamp(flag, 0, session.Flags.Count - 1) : 0;
        var episodes = session.CurrentFlag?.Episodes;
        session.EpisodeIndex = episodes is { Count: > 0 } ? Math.Clamp(ep, 0, episodes.Count - 1) : 0;
        var cid = Engine.VodConfigService.Cid;
        var key = (site?.Key ?? "") + "@" + (vod?.Id ?? "");
        session.History = Stores.FindHistory(cid, key) ?? new Models.History
        {
            Key = key,
            Cid = cid,
            VodName = vod?.CleanName ?? "",
            VodPic = vod?.Pic ?? "",
            VodFlag = session.CurrentFlag?.Flag ?? "",
            CreateTime = Stores.Now(),
        };
        return session;
    }
}

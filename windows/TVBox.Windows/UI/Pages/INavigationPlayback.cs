namespace TVBoxForWindows.UI.Pages;

/// <summary>由常驻侧栏 Frame 在隐藏播放页前调用；只暂停，不销毁页面状态。</summary>
public interface INavigationPlayback
{
    void PauseForNavigation();
    void ActivateAfterNavigation();
    void SynchronizePlaybackWindow();
}

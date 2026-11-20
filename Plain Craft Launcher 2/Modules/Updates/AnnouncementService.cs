using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

public static class AnnouncementService
{
    public static void Load()
    {
        // 0 = 全部显示，1 = 仅重要，2 = 完全关闭
        var solution = States.System.AnnounceSolution;
        if (solution > 1) return;

        var showedAnnounced = States.Hint.ShowedAnnouncements
            .Split("|".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var showAnnounce = UpdateManager.remoteServer.GetAnnouncementList().Content
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            // 选了「仅重要」的人只该看到 important: true 的那几条
            .Where(x => solution != 1 || x.Important)
            .Where(x => !showedAnnounced.Contains(x.Id))
            .ToList();

        ModBase.Log("[System] 需要展示的公告数量：" + showAnnounce.Count);

        ModBase.RunInNewThread(() =>
        {
            foreach (var item in showAnnounce)
            {
                var first = _GetWebAction(item.Btn1) ? item.Btn1 : null;
                var second = _GetWebAction(item.Btn2) ? item.Btn2 : null;
                // Primary buttons are always visible. A missing first action
                // therefore becomes the close action rather than an empty button.
                ModMain.MyMsgBox(item.Detail, item.Title,
                    first is null ? Lang.Text("Common.Action.Close") : first.Text,
                    second?.Text ?? "",
                    first is null ? "" : Lang.Text("Common.Action.Close"),
                    button1Action: first is null ? null : () => CustomEvent.Raise(EventType.OpenUrl, first.CommandParameter),
                    button2Action: second is null ? null : () => CustomEvent.Raise(EventType.OpenUrl, second.CommandParameter));
            }
        });

        showedAnnounced.AddRange(showAnnounce.Select(x => x.Id));
        showedAnnounced = showedAnnounced.Distinct().ToList();
        States.Hint.ShowedAnnouncements = showedAnnounced.Join("|");
    }

    private static bool _GetWebAction(AnnouncementBtnInfoModel? button) =>
        button is not null &&
        EventTypeMapper.TryParse(button.Command, out var type) && type == EventType.OpenUrl &&
        Uri.TryCreate(button.CommandParameter, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;
}

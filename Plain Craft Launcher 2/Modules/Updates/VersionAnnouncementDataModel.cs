using System.Text.Json.Serialization;

namespace PCL;

public class VersionAnnouncementDataModel
{
    [JsonPropertyName("content")]
    public List<VersionAnnouncementContentModel> Content { get; set; }
}

public class VersionAnnouncementContentModel
{
    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }

    /// <summary>
    ///     是否重要。设置里选「仅重要时显示」的用户只会看到这类公告。
    ///     用于「某个版本有严重问题，别升」这种必须让所有人知道的消息；
    ///     活动、预告之类留 <c>false</c>。
    /// </summary>
    [JsonPropertyName("important")]
    public bool Important { get; set; }

    [JsonPropertyName("btn1")]
    public AnnouncementBtnInfoModel Btn1 { get; set; }

    [JsonPropertyName("btn2")]
    public AnnouncementBtnInfoModel Btn2 { get; set; }
}

public class AnnouncementBtnInfoModel
{
    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; }

    [JsonPropertyName("command_paramter")]
    public string CommandParameter { get; set; }
}
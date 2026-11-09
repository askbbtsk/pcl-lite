using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using SearchOption = System.IO.SearchOption;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceCompResource
{



    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchBox.Text);


    public void SearchRun(object sender, EventArgs e)
    {
        var curToken = new CancellationTokenSource();
        var oldToken = Interlocked.Exchange(ref _cancelToken, curToken);
        oldToken?.Cancel();
        oldToken?.Dispose();

        // this exception is ignored
        Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                var token = curToken.Token;
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;
                if (IsSearching)
                {
                    var searchText = SearchBox.Text;
                    searchResult = await Task.Run(() => GetSearchResult(searchText), token);
                }

                if (token.IsCancellationRequested) return;
                RefreshUI();
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "搜索过程中发生异常");
            }
        }));
    }


    private List<ModLocalComp.LocalCompFile> GetSearchResult(string query)
    {
        // 构造请求
        var queryList = new List<ModBase.SearchEntry<ModLocalComp.LocalCompFile>>();
        foreach (var Entry in ModLocalComp.compResourceListLoader.output.AsReadOnly())
        {
            var searchSource = new List<ModBase.SearchSource>();
            searchSource.Add(new ModBase.SearchSource(Entry.Name, 1d));
            searchSource.Add(new ModBase.SearchSource(Entry.FileName, 1d));
            if (Entry.Version is not null) searchSource.Add(new ModBase.SearchSource(Entry.Version, 0.2d));
            if (Entry.Description is not null && !string.IsNullOrEmpty(Entry.Description))
                searchSource.Add(new ModBase.SearchSource(Entry.Description, 0.4d));
            if (Entry.Comp is not null)
            {
                if ((Entry.Comp.RawName ?? "") != (Entry.Name ?? ""))
                    searchSource.Add(new ModBase.SearchSource(Entry.Comp.RawName, 1d));
                if ((Entry.Comp.TranslatedName ?? "") != (Entry.Comp.RawName ?? ""))
                    searchSource.Add(new ModBase.SearchSource(Entry.Comp.TranslatedName, 1d));
                if ((Entry.Comp.Description ?? "") != (Entry.Description ?? ""))
                    searchSource.Add(new ModBase.SearchSource(Entry.Comp.Description, 0.4d));
                searchSource.Add(new ModBase.SearchSource(string.Join("", Entry.Comp.Tags), 0.2d));
            }

            queryList.Add(new ModBase.SearchEntry<ModLocalComp.LocalCompFile>
                { item = Entry, searchSource = searchSource });
        }

        // 进行搜索
        return ModBase.Search(queryList, query, ModBase.MaxLocalSearchDepth, 0.35d).Select(r => r.item).ToList();
    }

}

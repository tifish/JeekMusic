using Avalonia.Controls;
using JeekMusic.Library;

namespace JeekMusic.UI;

public partial class MainWindow
{
    void AddLanguageFilterActions(MenuFlyout menu)
    {
        var service=_app.SingingLanguages;
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header=service.StatusText,IsEnabled=false });
        var start=new MenuItem { Header=L.T("识别整个曲库的演唱语言…"),Name="AnalyzeSingingLanguages",
            IsEnabled=!service.Busy && _app.Library.Snapshot.Tracks.Count>0 };
        start.Click+=async(_,_)=>await BeginSingingLanguages(_app.Library.Snapshot.Tracks.ToArray());
        menu.Items.Add(start);
        if(service.Busy) {
            var cancel=new MenuItem { Header=L.T("取消语种识别"),Name="CancelSingingLanguages" };
            cancel.Click+=(_,_)=>service.Cancel(); menu.Items.Add(cancel);
        }
    }
    async Task BeginSingingLanguages(IReadOnlyList<Track> tracks,bool force=false)
    {
        if(tracks.Count==0) return;
        var confirmed=false;
        if(!_app.SingingLanguages.ModelReady) {
            confirmed=await Dialogs.Confirm(this,L.T("识别演唱语言"),
                L.T("首次使用需下载约 78 MB 的本地识别模型。音频不会上传；按音频取样识别，可能存在误判，可右键手工纠正。是否下载并开始？"));
            if(!confirmed) return;
        }
        try { _app.SingingLanguages.Start(tracks,confirmed,force); _vm.ShowToast(_app.SingingLanguages.StatusText); }
        catch(Exception) { await Dialogs.Info(this,L.T("识别演唱语言"),L.T("无法开始识别，请检查模型、文件可用性或当前任务状态。")); }
    }
    internal MenuItem SingingLanguageMenu(IReadOnlyList<Track> tracks)
    {
        var targets=tracks.DistinctBy(t=>t.TrackKey).ToArray();
        var menu=new MenuItem { Header=L.T("演唱语言"),Name="SingingLanguageMenu",IsEnabled=targets.Length>0 };
        var results=targets.Select(_app.SingingLanguages.Result).ToArray();
        var summary=results.Select(LanguageSummary).Distinct().ToArray();
        menu.Items.Add(new MenuItem { Header=summary.Length==1?summary[0]:L.T("多种演唱语言或来源"),IsEnabled=false });
        foreach(var (label,value) in new[] { ("中文","CHINESE"),("英文","ENGLISH"),("中文和英文","CHINESE_ENGLISH"),
            ("其他语种","OTHER"),("纯音乐","INSTRUMENTAL"),("未知语种","UNKNOWN"),("恢复自动识别","AUTO") }) {
            var option=new MenuItem { Header=L.T(label),Name="SingingLanguage"+value,ToggleType=MenuItemToggleType.Radio,
                IsChecked=value=="AUTO"?results.All(r=>r?.Source!="MANUAL"):results.All(r=>r?.Source=="MANUAL" && SameLanguage(r,SingingLanguage.Manual(value))) };
            option.Click+=async(_,_)=> {
                try { _app.SingingLanguages.SetManual(targets,value); }
                catch(Exception) { await Dialogs.Info(this,L.T("演唱语言"),L.T("保存失败，歌曲可能已改变，请刷新后重试。")); }
            };
            menu.Items.Add(option);
        }
        menu.Items.Add(new Separator());
        var analyze=new MenuItem { Header=L.T("识别所选歌曲的演唱语言…"),Name="AnalyzeSelectedSingingLanguages",IsEnabled=!_app.SingingLanguages.Busy };
        analyze.Click+=async(_,_)=>await BeginSingingLanguages(targets,true);
        menu.Items.Add(analyze);
        return menu;
    }
    static bool SameLanguage(SingingLanguage a,SingingLanguage b)=>a.State==b.State && a.Languages.Order().SequenceEqual(b.Languages.Order());
    static string LanguageSummary(SingingLanguage? result)
    {
        if(result is null) return L.T("演唱语种尚未识别");
        var text=result.State switch { "instrumental"=>L.T("纯音乐"),"unknown"=>L.T("未知语种"),
            _=>string.Join(" + ",result.Languages.Select(l=>l is "zh" or "yue"?L.T("中文"):l=="en"?L.T("英文"):l=="other"?L.T("其他语种"):l.ToUpperInvariant())) };
        return text+" · "+L.T(result.Source=="MANUAL"?"手工标注":"自动");
    }
}

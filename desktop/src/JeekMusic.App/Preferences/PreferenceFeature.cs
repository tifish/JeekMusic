using Avalonia.Controls;
using Avalonia;
using Avalonia.Media;
using Avalonia.Input.Platform;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using JeekMusic.UI;
using static JeekMusic.Core.Preferences.PreferenceProtocol;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Preferences;

public sealed class PreferenceFeature : IFeature
{
    public void Start(AppServices app,Shell shell)=>Register(L.T("标签学习"),75,Build);
    internal static string DimensionText(string d)=>L.T(d switch { "MOOD"=>"运动","VOICE"=>"人声","GENRE"=>"古典",_=>"安静" });
    internal static string StateText(string s)=>L.T(s switch {
        "active"=>"已生效", "updated"=>"歌曲识别已更新", "updated_with_uncovered"=>"已更新，部分歌曲未覆盖", "updating_library"=>"正在更新歌曲识别",
        "idle"=>"尚未开始", "checking_material"=>"正在检查标注", "learning" or "running"=>"正在学习", "waiting_playback"=>"等待播放暂停",
        "preparing_components"=>"正在准备本地学习", "download_required"=>"首次学习需要下载", "cancelled"=>"已取消，可继续", "interrupted"=>"上次中断，可继续",
        "insufficient_material"=>"标注还不够", "validation_failed"=>"效果检查未通过，使用原有识别", "check_material_required" or "self_selected_candidate" or "waiting_checks"=>"缺少效果检查材料，使用原有识别",
        "development_required" or "regression_required" or "independent_test_required"=>"等待效果检查", "training_gate_passed"=>"已学习，等待效果检查",
        "invalidated"=>"标注已变化，等待重新学习", "incomplete"=>"尚未完成，可继续", "failed"=>"未完成，可重试", "synthetic_validation_complete"=>"测试流程完成，未启用",_=>"等待处理" });
    static Control Build(SettingsContext c)
    {
        var s=c.App.Preferences;
        var panel=new StackPanel { Spacing=12 };
        panel.Children.Add(Note(L.T("在歌曲列表修正运动、人声、古典或安静标签，即可用于学习。四个维度分别检查，通过的维度会自动生效并更新全库。你手工修正的标签始终优先。")));
        var status=Note(""); panel.Children.Add(status);
        var failureReason=Note(""); failureReason.Name="PreferenceFailureReason"; panel.Children.Add(failureReason);
        var diagnosticText=new TextBox { Name="PreferenceFailureDiagnostics",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=180 };
        var diagnosticPanel=new StackPanel { Spacing=6 }; diagnosticPanel.Children.Add(diagnosticText);
        diagnosticPanel.Children.Add(Action(L.T("复制脱敏诊断"),async()=> { if(c.Window.Clipboard is { } clipboard) await clipboard.SetTextAsync(diagnosticText.Text??""); }));
        var diagnosticExpander=new Expander { Header=L.T("高级诊断（不含歌曲、标签或路径）"),Content=diagnosticPanel,Name="PreferenceFailureAdvanced" }; panel.Children.Add(diagnosticExpander);
        var rows=new StackPanel { Spacing=10 };
        var message=Note("");
        var start=Action(L.T("开始学习"),()=>{}); start.Name="PreferenceStartLearning";
        var stop=Action(L.T("取消学习"),()=>s.Training.Cancel()); stop.Name="PreferenceCancelLearning";
        panel.Children.Add(start); panel.Children.Add(stop);
        panel.Children.Add(message); panel.Children.Add(rows);
        bool busy=false;
        async void Run(Func<Task> action) { if(busy) return; busy=true; try { await action(); message.Text=""; } catch(Exception) { message.Text=L.T("学习操作未完成，请检查当前状态后重试。"); } finally { busy=false; Refresh(); } }
        start.Click+=(_,_)=>Run(async()=> {
            var state=s.Training.LearningStatus(); var confirm=S(state["state"])=="download_required";
            if(confirm && !await Dialogs.Confirm(c.Main,L.T("下载本地学习文件"),L.T(S(state["detail"])),L.T("下载并开始学习"))) return;
            s.Training.StartLearning(confirm);
        });
        panel.Children.Add(Note(L.T("学习在这台电脑上进行，播放优先。关闭或取消后可继续，已完成的歌曲分析会保留。未通过检查或尚未覆盖的歌曲继续使用原有识别。已配对手机自动同步标注和识别结果。")));
        panel.Children.Add(Action(L.T("导出学习数据"),()=>Run(async()=> {
            var file=await c.Window.StorageProvider.SaveFilePickerAsync(new() { Title=L.T("导出学习数据"),SuggestedFileName="personal-preference.json" });
            if(file?.TryGetLocalPath() is { } path) s.Store.Export(s.ActiveProfile,path);
        })));
        panel.Children.Add(Action(L.T("清除学习数据"),()=>Run(async()=> {
            if(await Dialogs.Confirm(c.Main,L.T("清除学习数据"),L.T("清除全部学习标注和学习结果，并同步到已配对设备。日常手工标签、曲库、播放列表和配对保持不变。确认清除？"),L.T("清除"))) s.DeleteProfile();
        })));
        void Refresh()
        {
            var snapshot=s.Status(); var workflow=snapshot["learning"]!; var running=workflow["running"]?.GetValue<bool>()==true;
            status.Text=StateText(S(workflow["state"]));
            var failure=workflow["failure"];
            failureReason.IsVisible=failure!=null; diagnosticExpander.IsVisible=failure!=null;
            failureReason.Text=failure==null?"":L.T(S(failure["message"]))+"\n"+L.T(S(failure["nextStep"]));
            var diagnostics=failure==null?"":LearningFailure.Diagnostics(failure); if(diagnosticText.Text!=diagnostics) diagnosticText.Text=diagnostics;
            if(failure==null) status.Text+="\n"+L.T(S(workflow["detail"]));
            if(workflow["total"]?.GetValue<int>()>0) status.Text+=L.F($"\n已处理 {workflow["processed"]} / {workflow["total"]} · 未覆盖 {workflow["uncovered"]}");
            start.IsEnabled=!busy && !running; stop.IsVisible=running;
            start.Content=L.T(S(workflow["state"])=="download_required"?"下载并开始学习":S(workflow["state"]) is "cancelled" or "interrupted" or "incomplete"?"继续学习":S(workflow["state"])=="failed"?"重试学习":"开始学习");
            rows.Children.Clear();
            foreach(var head in snapshot["heads"]!.AsArray())
            {
                var d=S(head!["dimension"]); var material=head["material"]!;
                var available=material["available"]!.GetValue<int>();
                var state=head["enabled"]?.GetValue<bool>()==true?"active":S(head["learning"]?["state"]);
                var box=new StackPanel { Spacing=3 };
                box.Children.Add(SubHeading(DimensionText(d)));
                box.Children.Add(Note(L.F($"已标 {head["annotatedCount"]} · 可用 {available} · 正向 {material["positive"]} · 反向 {material["negative"]} · 待处理分歧 {head["conflictCount"]}")));
                box.Children.Add(Note(StateText(state)));
                if(head["learning"]?["failure"] is { } reason) {
                    box.Children.Add(Note(L.T(S(reason["message"]))+"\n"+L.T(S(reason["nextStep"]))));
                    box.Children.Add(Note(S(reason["stage"])+" · "+S(reason["code"])));
                }
                if(material["ready"]?.GetValue<bool>()!=true) box.Children.Add(Note(L.F($"总计还需至少 {material["missingTotal"]} 条；其中正向还需 {material["missingPositive"]} 条，相反标注还需 {material["missingNegative"]} 条。在歌曲列表日常修正即可。")));
                else if(head["enabled"]?.GetValue<bool>()!=true && state is "check_material_required" or "self_selected_candidate") box.Children.Add(Note(L.T("效果检查尚缺独立材料，当前继续使用原有识别。")));
                if(material["missingLocal"]!.GetValue<int>()>0) box.Children.Add(Note(L.F($"有 {material["missingLocal"]} 首标注歌曲尚未在电脑找到相同录音，请连接对应音乐目录后重试。")));
                if(material["missingComponents"]!.GetValue<int>()>0 && available>0) box.Children.Add(Note(L.T("歌曲来源还太集中，继续修正更多不同艺人、不同专辑的歌曲。")));
                if(material["missingDualArtists"]!.GetValue<int>()>0 && available>0) box.Children.Add(Note(L.F($"还需 {material["missingDualArtists"]} 位艺人各有适合与不适合运动的歌曲标注。")));
                rows.Children.Add(box);
            }
        }
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) }; timer.Tick+=(_,_)=>Refresh(); timer.Start();
        panel.DetachedFromVisualTree+=(_,_)=>timer.Stop(); Refresh(); return panel;
    }
}

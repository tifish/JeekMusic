using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using JeekMusic.UI;
using static JeekMusic.Core.Preferences.PreferenceProtocol;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Preferences;

public sealed class PreferenceFeature : IFeature
{
    public void Start(AppServices app, Shell shell) => Register(L.T("个人标签学习"),75,c=>Build(c));
    static string PositiveText(string d)=>d switch { "MOOD"=>L.T("适合运动"),"VOICE"=>L.T("有人声"),"GENRE"=>L.T("古典"),_=>L.T("安静") };
    static string NegativeText(string d)=>d switch { "MOOD"=>L.T("不适合运动"),"VOICE"=>L.T("纯器乐"),"GENRE"=>L.T("非古典"),_=>L.T("非安静") };
    static string StateText(string state)=>L.T(state switch {
        "idle"=>"未开始", "insufficient_material"=>"材料不足", "components_required"=>"需要准备组件", "identity_incomplete"=>"身份清单未完整冻结", "preparing_components"=>"正在准备公共组件", "running"=>"运行中", "cancelled"=>"已取消", "failed"=>"失败", "incomplete"=>"未完成（预算或材料）", "interrupted"=>"上次工作进程已中断", "validation_failed"=>"验证未通过", "synthetic_validation_complete"=>"合成链路验证完成（非用户真值）", "extraction_complete"=>"本批表示已提取", "components_ready"=>"公共组件已就绪", "training_gate_passed"=>"训练门槛通过（候选未启用）", _=>state });
    static string AnswerText(string answer)=>answer switch { "SPORT"=>PositiveText("MOOD"),"NON_SPORT"=>NegativeText("MOOD"),"VOCAL"=>PositiveText("VOICE"),"INSTRUMENTAL"=>NegativeText("VOICE"),"CLASSICAL"=>PositiveText("GENRE"),"NON_CLASSICAL"=>NegativeText("GENRE"),"CALM"=>PositiveText("CALMNESS"),"NON_CALM"=>NegativeText("CALMNESS"),"UNCERTAIN"=>L.T("不确定"),"UNRESOLVED"=>L.T("版本不对 / 未听清"),"SKIP"=>L.T("跳过"),"WITHDRAWN"=>L.T("撤回"),_=>answer };
    static WrapPanel Wrap(params Control[] controls)
    {
        var panel=new WrapPanel(); foreach(var control in controls) { control.Margin=new Thickness(0,0,8,8); panel.Children.Add(control); } return panel;
    }
    Control Build(SettingsContext c)
    {
        var service=c.App.Preferences;
        var panel=new StackPanel { Spacing=10 };
        panel.Children.Add(Note(L.T("独立的人工标注档案，不修改日常四维手工标签。题目隐藏自动结果；不会替换播放队列。当前不提供隔离试听，只有能确认整首及版本时才选整曲依据。")));
        var message=new TextBlock { TextWrapping=Avalonia.Media.TextWrapping.Wrap }; panel.Children.Add(message);
        var profiles=new ComboBox { Name="PreferenceProfiles",HorizontalAlignment=HorizontalAlignment.Stretch,MinWidth=0 };
        ComboBox? dimensionChoice=null;
        var drafts=new Dictionary<string,(int Basis,string Reason)>();
        var status=Note(""); var training=Note(""); var batchPanel=new StackPanel { Spacing=12 }; bool refreshing=false;
        async void Run(Func<Task> action)
        {
            try { await action(); Refresh(); } catch(Exception e) { message.Text=e.Message; }
        }
        var profileName=new TextBox { Name="PreferenceProfileName",PlaceholderText=L.T("档案名称"),Width=200 };
        panel.Children.Add(Wrap(profileName,Action(L.T("创建档案"),()=>Run(()=> { service.CreateProfile(profileName.Text??""); profileName.Text=""; return Task.CompletedTask; }))));
        var profileRows=service.Status()["profiles"]!.AsArray();
        var live=profileRows.Where(n=>n?["deleted"]?.GetValue<bool>()!=true).ToArray();
        foreach(var p in live) profiles.Items.Add(S(p?["name"])+" · "+S(p?["profileId"])[..8]);
        profiles.SelectedIndex=Array.FindIndex(live,p=>S(p?["profileId"])==service.ActiveProfile);
        profiles.SelectionChanged+=(_,_)=> { if(!refreshing && profiles.SelectedIndex>=0) Run(()=> { service.SelectProfile(S(live[profiles.SelectedIndex]?["profileId"])); return Task.CompletedTask; }); };
        panel.Children.Add(Note(L.T("当前档案（同步不会自动混入）"))); panel.Children.Add(profiles);
        dimensionChoice=Choice(new[] {("MOOD",L.T("运动 / 非运动")),("VOICE",L.T("人声 / 纯器乐")),("GENRE",L.T("古典 / 非古典")),("CALMNESS",L.T("安静 / 非安静"))},service.ActiveDimension,d=> { if(!refreshing) Run(()=> { service.SelectDimension(d); return Task.CompletedTask; }); });
        dimensionChoice.Name="PreferenceDimension"; panel.Children.Add(Note(L.T("独立标注维度"))); panel.Children.Add(dimensionChoice);
        panel.Children.Add(Note(L.T("每首可只标一个维度，其他维度保持未标。GENRE 的 ClassicalTitles 强制自动古典优先于个人预测；手工覆盖最终优先。旧 QUIET 只兼容为非运动，不推导安静。")));
        panel.Children.Add(status);
        panel.Children.Add(Wrap(Action(L.T("准备 / 继续六首批次"),()=>Run(async()=> { await service.PrepareBatch(); })),Action(L.T("刷新 / 续接"),()=>Refresh())));
        panel.Children.Add(Action(L.T("核验本批本机录音身份（最多六个文件）"),()=>Run(async()=> { var result=await service.MatchLocalBatch(); message.Text=L.F($"已核验 {result["checkedFiles"]} 个候选文件，匹配 {result["matched"]} 个；缺少本地文件的引用保留。仅按完整内容 SHA-256 绑定。"); })));
        panel.Children.Add(Wrap(Action(L.T("登记下六首身份（不标注）"),()=>Run(async()=> { await service.PrepareBatch(inventoryOnly:true); })),Action(L.T("冻结完整研究清单"),()=>Run(()=> { service.FreezeStudy(); return Task.CompletedTask; }))));
        panel.Children.Add(batchPanel);
        panel.Children.Add(SubHeading(L.T("本地训练（候选不会自动启用）")));
        panel.Children.Add(Note(L.T("运动：24/36/48节点，至少16条、每类8条、8组件及3个双类艺人。其余各维：12/24/36节点，至少12条、每类6条、6组件；开发8题、封存24题，分别验证。共用表示和隔离清单，不要求四维全标。旧11首仅作运动回归。")));
        panel.Children.Add(Note(L.T("按需组件：官方 CLAP 权重约615 MB，另需 CPU PyTorch / Transformers 私有运行时，可能数 GB 磁盘及网络；需本机 Python 3.13+ 和 FFmpeg。CPU每次最多六首、两线程、240秒、监控2 GiB。准备组件只下载公共组件，不上传音频。")));
        panel.Children.Add(training);
        panel.Children.Add(Wrap(Action(L.T("准备官方组件"),()=>Run(()=> { service.Training.Start("prepare"); return Task.CompletedTask; })),Action(L.T("提取 / 续接（最多6首）"),()=>Run(()=> { service.Training.Start("extract"); return Task.CompletedTask; }))));
        panel.Children.Add(Wrap(Action(L.T("训练 / 检查材料"),()=>Run(()=> { service.Training.Start("train"); return Task.CompletedTask; })),Action(L.T("取消工作进程"),()=>service.Training.Cancel())));
        panel.Children.Add(Action(L.T("导入本档案的既有运动回归反馈"),()=>Run(async()=> {
            var files=await c.Window.StorageProvider.OpenFilePickerAsync(new() { Title=L.T("选择本机反馈文件（不会作为新训练真值）"),AllowMultiple=false });
            if(files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            if(await Dialogs.Confirm(c.Main,L.T("确认反馈归属与版本"),L.T("请确认这11首明确运动反馈属于当前档案的同一个人，且当前本机录音版本仍对应原反馈。只导入回归用途，绝不用于训练或其他维度。"),L.T("确认导入回归"))) await service.ImportWorkoutRegression(path,true);
        })));
        panel.Children.Add(Action(L.T("提取旧反馈回归（最多六首，不参与拟合）"),()=>Run(()=> { service.Training.Start("regression_extract"); return Task.CompletedTask; })));
        panel.Children.Add(Action(L.T("验收当前冻结阶段（仅完整批次后一次性开启）"),()=>Run(()=> { service.Training.Start("evaluate"); return Task.CompletedTask; })));
        panel.Children.Add(SubHeading(L.T("同步、导出与删除")));
        panel.Children.Add(Note(L.T("已配对同一人的设备通过 LAN 主动交换增量修订。其他档案需主动选择；并发答案保留冲突，撤回与删除会传播并使模型失效。手机查看训练状态，不执行 Windows 产物。目录 format 2 兼容仍保留。")));
        panel.Children.Add(Action(L.T("导出当前档案"),()=>Run(async()=> {
            var file=await c.Window.StorageProvider.SaveFilePickerAsync(new() { Title=L.T("导出私有标注档案"),SuggestedFileName="personal-preference.json" });
            if(file?.TryGetLocalPath() is { } path) { service.Store.Export(service.ActiveProfile,path); message.Text=L.T("已导出私有档案（包含个人标签，请妥善保管）。"); }
        })));
        panel.Children.Add(Action(L.T("删除当前档案"),()=>Run(async()=> { if(await Dialogs.Confirm(c.Main,L.T("删除个人档案"),L.T("删除将传播到已配对设备，并使派生候选失效。确认删除？"),L.T("删除"))) service.DeleteProfile(); })));

        Control IdentityEditor(System.Text.Json.Nodes.JsonNode recording,string id)
        {
            var work=new TextBox { Text=S(recording["workGroup"]),PlaceholderText=L.T("作品身份（版本/翻唱归同组）") };
            var album=new TextBox { Text=S(recording["albumGroup"]),PlaceholderText=L.T("发行专辑身份") };
            var artists=new TextBox { Text=string.Join(";",ReadStrings(recording["artists"])),PlaceholderText=L.T("艺人规范身份，分号分隔；含合作/别名/成员关系") };
            var exposed=new CheckBox { Content=L.T("此录音此前已用于研究/规则/选模型"),IsChecked=recording["exposed"]?.GetValue<bool>()==true };
            return new Expander { Header=S(recording["title"])+L.T(" · 身份核验"),Content=new StackPanel { Spacing=4,Children={ work,album,artists,exposed,Action(L.T("确认这些身份关系"),()=>Run(()=> { service.ReviewIdentity(id,work.Text??"",album.Text??"",(artists.Text??"").Split(';',StringSplitOptions.RemoveEmptyEntries),exposed.IsChecked==true); return Task.CompletedTask; })) } } };
        }
        void Refresh()
        {
            if(refreshing) return; refreshing=true;
            try
            {
                var s=service.Status();
                if(dimensionChoice!=null) dimensionChoice.SelectedIndex=Array.IndexOf(Dimensions,service.ActiveDimension);
                live=s["profiles"]!.AsArray().Where(n=>n?["deleted"]?.GetValue<bool>()!=true).ToArray();
                profiles.Items.Clear(); foreach(var p in live) profiles.Items.Add(S(p?["name"])+" · "+S(p?["profileId"])[..8]);
                profiles.SelectedIndex=Array.FindIndex(live,p=>S(p?["profileId"])==service.ActiveProfile);
                status.Text=L.F($"已答 {s["answered"]} · 不确定/版本未定 {s["uncertain"]} · 冲突 {s["conflicts"]} · 可用整曲训练答案 {s["trainingLabels"]}");
                var ts=service.Training.Status(); training.Text=L.F($"状态：{StateText(S(ts["state"]))}  {L.T(S(ts["detail"]))}");
                batchPanel.Children.Clear();
                if(service.ActiveProfile.Length==0 || service.Deleted(service.ActiveProfile)) return;
                var inventory=service.Inventory();
                batchPanel.Children.Add(Note(L.F($"身份档案：已登记 {inventory["registered"]} · 已复核 {inventory["reviewed"]} · 清单 {inventory["studyState"]}")));
                foreach(var entry in inventory["items"]!.AsArray()) batchPanel.Children.Add(IdentityEditor(entry!["metadata"]!,S(entry["recordingId"])));
                var batch=service.Batch();
                batchPanel.Children.Add(Note(L.T("批次：")+(S(batch["state"]).StartsWith("frozen_")?L.T("已冻结"):S(batch["state"]))+L.T("。未核验完整身份图时仅为训练侧非独立试行，开发/测试保持锁定。")));
                batchPanel.Children.Add(new TextBlock { Text=L.T(S(batch["question"])),TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontWeight=Avalonia.Media.FontWeight.SemiBold });
                foreach(var item in batch["items"]!.AsArray())
                {
                    var id=S(item?["recordingId"]);
                    var card=new StackPanel { Spacing=5 };
                    card.Children.Add(new TextBlock { Text=$"{S(item?["title"])} — {S(item?["artist"])}\n{S(item?["album"])} · {item?["durationMs"]} ms",TextWrapping=Avalonia.Media.TextWrapping.Wrap });
                    card.Children.Add(Note(L.T("当前答案：")+AnswerText(S(item?["answer"]))+(item?["conflict"]?.GetValue<bool>()==true ? L.T(" · 存在并发冲突，需核对后复核") : "")));
                    var draftKey=service.ActiveProfile+":"+service.ActiveDimension+":"+S(batch["manifestId"])+":"+id;
                    var savedBasis=Array.IndexOf(Bases,S(item?["basis"]));
                    var draft=drafts.GetValueOrDefault(draftKey,(savedBasis<0?3:savedBasis,S(item?["reason"])));
                    var basis=new ComboBox { MinWidth=240,ItemsSource=new[] { L.T("熟悉并能确认整首及版本"),L.T("完整听过"),L.T("仅片段"),L.T("尚未确认") },SelectedIndex=draft.Item1 };
                    var reason=new TextBox { PlaceholderText=L.T("可选理由（最多500字）"),MaxLength=500,Text=draft.Item2 };
                    basis.SelectionChanged+=(_,_)=>drafts[draftKey]=(basis.SelectedIndex,reason.Text??"");
                    reason.TextChanged+=(_,_)=>drafts[draftKey]=(basis.SelectedIndex,reason.Text??"");
                    card.Children.Add(basis); card.Children.Add(reason);
                    var conflict=item?["conflict"]?.GetValue<bool>()==true;
                    if(conflict) foreach(var revision in service.Conflicts()["items"]!.AsArray().Where(n=>S(n?["recordingId"])==id).SelectMany(n=>n!["revisions"]!.AsArray())) card.Children.Add(Note($"{S(revision?["deviceId"])} · {S(revision?["answer"])} · {S(revision?["basis"])}"));
                    void Save(string answer)=>Run(()=> { service.Answer(id,answer,Bases[basis.SelectedIndex],reason.Text??"",conflict); drafts.Remove(draftKey); return Task.CompletedTask; });
                    card.Children.Add(Wrap(Action((conflict?L.T("复核："):"")+PositiveText(service.ActiveDimension),()=>Save(Labels(service.ActiveDimension).Positive)),Action((conflict?L.T("复核："):"")+NegativeText(service.ActiveDimension),()=>Save(Labels(service.ActiveDimension).Negative)),Action(L.T("不确定"),()=>Save("UNCERTAIN"))));
                    card.Children.Add(Wrap(Action(L.T("版本不对 / 未听清"),()=>Save("UNRESOLVED")),Action(L.T("跳过"),()=>Save("SKIP")),Action(L.T("撤回"),()=>Save("WITHDRAWN"))));
                    var recording=service.Store.Current(service.ActiveProfile,"recording").SingleOrDefault(e=>S(e["entityId"])==id)?["payload"];
                    if(recording!=null) card.Children.Add(IdentityEditor(recording,id));
                    batchPanel.Children.Add(new Border { BorderThickness=new Thickness(1),BorderBrush=Avalonia.Media.Brushes.Gray,Padding=new Thickness(10),CornerRadius=new CornerRadius(5),Child=card });
                }
            }
            catch(Exception e) { message.Text=e.Message; }
            finally { refreshing=false; }
        }
        void Changed()=>Dispatcher.UIThread.Post(Refresh);
        service.Changed+=Changed; panel.DetachedFromVisualTree+=(_,_)=>service.Changed-=Changed;
        Refresh(); return panel;
    }
}

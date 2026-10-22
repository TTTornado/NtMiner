using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NTMiner.Modern.WinUI;

/// <summary>
/// 挖矿配置页的交互：内核参数复选框、实时保存与命令行预览。
/// 参数列表来自 MinerSwitchCatalog，勾选结果写回 MiningOptions.KernelSwitches。
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, CheckBox> _switchBoxes = new(StringComparer.Ordinal);
    private bool _suppressAutoSave;
    private bool _switchesBuilt;

    /// <summary>自动保存成功后记下的快照，用于判断是否还有未落盘的改动。</summary>
    private string? _lastSavedOptions;

    /// <summary>按当前内核重建复选框。常用项直接展示，其余收进"高级设置"。</summary>
    private void RebuildSwitches()
    {
        var miner = SelectedMiner;
        var enabled = _switchBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        _switchBoxes.Clear();
        CommonSwitchPanel.Children.Clear();
        AdvancedSwitchPanel.Children.Clear();

        var definitions = MinerSwitchCatalog.For(miner);
        if (definitions.Count == 0)
        {
            SwitchHint.Text = "当前内核暂未提供可配置参数。";
            AdvancedSwitches.Visibility = Visibility.Collapsed;
            _switchesBuilt = true;
            UpdateCommandLinePreview();
            return;
        }

        SwitchHint.Text = "勾选后用鼠标悬停可查看对应的命令行参数。内核不支持的项目不会出现在这里。";
        var common = definitions.Where(d => !d.IsAdvanced).ToArray();
        var advanced = definitions.Where(d => d.IsAdvanced).ToArray();
        foreach (var definition in common) CommonSwitchPanel.Children.Add(CreateSwitchBox(miner, definition, enabled));
        foreach (var definition in advanced) AdvancedSwitchPanel.Children.Add(CreateSwitchBox(miner, definition, enabled));
        AdvancedSwitches.Visibility = advanced.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _switchesBuilt = true;
        UpdateCommandLinePreview();
    }

    private CheckBox CreateSwitchBox(MinerKind miner, MinerSwitch definition, HashSet<string> previous)
    {
        // 切换内核时保留同名开关的勾选状态；没有历史状态则用默认值。
        var isOn = previous.Count > 0 ? previous.Contains(definition.Name) : MinerSwitchCatalog.IsDefaultOn(miner, definition.Name);
        var box = new CheckBox
        {
            Content = definition.Description,
            IsChecked = isOn,
            Tag = definition.Name
        };
        ToolTipService.SetToolTip(box, definition.Argument);
        box.Click += (_, _) =>
        {
            UpdateCommandLinePreview();
            AutoSave();
        };
        _switchBoxes[definition.Name] = box;
        return box;
    }

    /// <summary>载入配置时按保存的开关名恢复勾选状态；缺失的项按默认值处理。</summary>
    private void RestoreSwitches(MiningOptions options)
    {
        RebuildSwitches();
        var saved = options.KernelSwitches.ToHashSet(StringComparer.Ordinal);
        foreach (var (name, box) in _switchBoxes)
            box.IsChecked = saved.Count > 0 ? saved.Contains(name) : box.IsChecked;
        UpdateCommandLinePreview();
    }
    private string[] EnabledSwitches() =>
        _switchBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToArray();

    /// <summary>
    /// 刷新命令行预览。这里展示的是按当前配置推导出的参数，与实际启动命令可能存在顺序差异，
    /// 但内容一致；界面需明确这一点，避免被误认为执行记录。
    /// </summary>
    private void UpdateCommandLinePreview()
    {
        if (!_switchesBuilt) return;
        var options = Options();
        CommandLinePreview.Text = MinerSwitchCatalog.Preview(options, EnabledSwitches());
        CommandLineNote.Text = options.DemoMode
            ? "演示模式不使用所选内核文件，参数仅作参考。"
            : "预览按当前选择推导，实际启动参数由适配器生成；开发费与内核默认行为以实际版本为准。";
    }

    /// <summary>
    /// 实时保存：任何字段改动都立即写入本机配置。
    /// 保存失败只提示，不打断输入；任务在运行时额外提示需要重启才生效。
    /// </summary>
    private async void AutoSave()
    {
        if (_suppressAutoSave || !_ready || _closing) return;
        try
        {
            var options = Options();
            var serialized = System.Text.Json.JsonSerializer.Serialize(options);
            if (serialized == _lastSavedOptions) return;
            await _store.SaveAsync(options);
            _lastSavedOptions = serialized;
            AutoSaveState.Text = $"已自动保存 {DateTime.Now:HH:mm:ss}";
            UpdateRestartHint();
        }
        catch (Exception ex)
        {
            AutoSaveState.Text = "自动保存失败：" + ex.Message;
            Feedback("配置未能保存：" + ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>任务在跑时，改动不会影响正在进行的会话，用黄色横幅说明需要重启。</summary>
    private void UpdateRestartHint()
    {
        var running = _state is MiningState.Starting or MiningState.Running or MiningState.Mining or MiningState.Recovering;
        var changed = _lastSavedOptions is not null && _activeRunOptions is not null && _lastSavedOptions != _activeRunOptions;
        RestartHint.IsOpen = running && changed;
    }

    /// <summary>启动任务时记录当次的配置，用于判断之后是否有改动需要重启才生效。</summary>
    private void RememberRunOptions(MiningOptions options) =>
        _activeRunOptions = System.Text.Json.JsonSerializer.Serialize(options);

    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCommandLinePreview();
        AutoSave();
    }

    private void NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateCommandLinePreview();
        AutoSave();
    }
}

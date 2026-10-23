using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NTMiner.Modern.WinUI;

public sealed partial class MainWindow
{
    private void RefreshImportedKernels(string? selectedPath)
    {
        var entries = _kernelLibrary.List();
        ImportedKernelChoice.ItemsSource = entries;
        var selected = entries.FirstOrDefault(e => string.Equals(e.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(selectedPath))
        {
            var oldDirectory = Path.GetDirectoryName(selectedPath);
            var oldId = oldDirectory is null ? "" : Path.GetFileName(oldDirectory);
            if (Guid.TryParseExact(oldId, "N", out _))
                selected = entries.FirstOrDefault(e => e.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
        }
        ImportedKernelChoice.SelectedItem = selected;
        KernelPath.Text = (ImportedKernelChoice.SelectedItem as ImportedKernel)?.ExecutablePath ?? "";
        UpdateControls();
    }

    private void ImportedKernelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        KernelPath.Text = (ImportedKernelChoice.SelectedItem as ImportedKernel)?.ExecutablePath ?? "";
        AlgorithmChoice.ItemsSource = null;
        AutoSave();
        UpdateControls();
    }

    private async Task ImportKernelAsync(string path)
    {
        var entry = await _kernelLibrary.ImportAsync(path);
        RefreshImportedKernels(entry.ExecutablePath);
        AutoSave();
    }

    private void DeleteKernelClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || HasActiveTask || _closing || ImportedKernelChoice.SelectedItem is not ImportedKernel selected) return;
        try
        {
            _kernelLibrary.Delete(selected);
            RefreshImportedKernels(null);
            AlgorithmText.Text = "";
            AlgorithmChoice.ItemsSource = null;
            AutoSave();
            Feedback($"已删除 {selected.DisplayName}。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Feedback("删除内核失败：" + ex.Message, InfoBarSeverity.Error); }
    }
}

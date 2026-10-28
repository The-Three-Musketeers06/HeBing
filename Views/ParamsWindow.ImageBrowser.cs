using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class ParamsWindow
{
    private bool _syncingImageSource;
    private string _pagingOperationId;
    private ImageBrowserSource PagingSource => _vm.GetImageBrowserSource(0, ImageSourcePicker.SelectedValue as string);

    private void RefreshImagePaging()
    {
        var sources = _vm.SelectedProgram?.Operations.Where(operation => operation.Driver == "LoadImage").ToArray() ?? [];
        _syncingImageSource = true;
        try
        {
            var selected = ImageSourcePicker.SelectedValue as string;
            if (_pagingOperationId != _vm.SelectedOperation?.Uuid)
            {
                selected = _vm.GetImageBrowserSource(_vm.GetSelectedPreviewIndex(), _vm.SelectedOperation?.Uuid)?.OperationId;
                _pagingOperationId = _vm.SelectedOperation?.Uuid;
            }
            if (ImageSourcePicker.ItemsSource is not Operation[] previous || !previous.SequenceEqual(sources)) ImageSourcePicker.ItemsSource = sources;
            ImageSourcePicker.SelectedItem = sources.FirstOrDefault(operation => operation.Uuid == selected) ?? sources.FirstOrDefault();
        }
        finally { _syncingImageSource = false; }
        var source = PagingSource;
        ImagePagingBar.Visibility = source?.Files.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        int index = source == null ? -1 : Array.FindIndex(source.Files, file => string.Equals(file, source.CurrentFile, StringComparison.OrdinalIgnoreCase));
        ImagePageText.Text = source == null ? "0 / 0" : $"{index + 1} / {source.Files.Length}";
        ImageSourcePicker.IsEnabled = _vm.CanEdit;
        PreviousImageButton.IsEnabled = _vm.CanEdit && index > 0;
        NextImageButton.IsEnabled = _vm.CanEdit && source != null && index >= 0 && index + 1 < source.Files.Length;
    }

    private void OnImageSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingImageSource && _vm != null) RefreshImagePaging();
    }

    private async Task ChangeImagePageAsync(int direction)
    {
        var source = PagingSource;
        if (source == null || !_vm.CanEdit) return;
        int index = Array.FindIndex(source.Files, file => string.Equals(file, source.CurrentFile, StringComparison.OrdinalIgnoreCase)) + direction;
        if (index < 0 || index >= source.Files.Length) return;
        EndRegionEditing(true); Preview.CancelRegionEdit();
        try { await _vm.SelectImageAndRunAsync(source.OperationId, source.Files[index]); TestStatus.Text = _vm.Status; }
        catch (Exception error) { TestStatus.Text = "切图失败：" + error.Message; }
        finally { RefreshImagePaging(); }
    }

    private async void OnPreviousImage(object sender, RoutedEventArgs e) => await ChangeImagePageAsync(-1);
    private async void OnNextImage(object sender, RoutedEventArgs e) => await ChangeImagePageAsync(1);
}

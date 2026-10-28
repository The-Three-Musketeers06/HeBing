using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text;

namespace AiVisionStudio.Views
{
    public partial class ImageView
    {
        private enum ThumbnailDisplayMode
        {
            //多张显示，单张隐藏
            Auto,
            //点x隐藏
            Hidden,
            //双击主图显示
            Shown
        }

        private ThumbnailDisplayMode _thumbnailMode = ThumbnailDisplayMode.Auto;

        private string _thumbnailSourceKey;
        private string _thumbnailOperationId;
        public event Action<string, string> ImageSelectionRequested;
        private ThumbnailItem[] _thumbnailItems = Array.Empty<ThumbnailItem>();
        //防止程序同步选中项时触发图片预览
        private bool _syscingThumbnails;

        private sealed class ThumbnailItem
        {
            public string FilePath { get; }
            private bool _previewLoaded;
            private BitmapSource _preview;
            public ThumbnailItem(string filePath)
            {
                FilePath = filePath;
            }

            public BitmapSource Preview
            {
                get
                {
                    if (_previewLoaded) return _preview;
                    _previewLoaded = true;
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 120;
                        bitmap.UriSource = new Uri(FilePath, UriKind.Absolute);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        _preview = bitmap;
                    }
                    catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException)
                    {
                        //单个文件不能生成缩略图，保留空白项
                    }
                    return _preview;
                }
            }
        }

        public void SetThumbnailSource(string sourceKey, IReadOnlyList<string> files, string currentFile, string operationId = null)
        {
            _thumbnailOperationId = operationId;
            sourceKey ??= "";
            var nextFiles = files?.ToArray() ?? Array.Empty<string>();
            //切换图片源时，才恢复默认显示规则
            //普通运行刷新不清除用户点击x的状态
            if (!string.Equals(_thumbnailSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            {
                _thumbnailSourceKey = sourceKey;
                _thumbnailMode = ThumbnailDisplayMode.Auto;
            }
            _syscingThumbnails = true;
            try
            {
                var oldFiles = _thumbnailItems.Select(item => item.FilePath);
                if (!nextFiles.SequenceEqual(oldFiles, StringComparer.OrdinalIgnoreCase))
                {
                    _thumbnailItems = nextFiles.Select(path => new ThumbnailItem(path)).ToArray();
                    ThumbnailList.ItemsSource = _thumbnailItems;
                }

                ThumbnailList.SelectedItem = _thumbnailItems.FirstOrDefault(item => string.Equals(item.FilePath, currentFile, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _syscingThumbnails = false;
            }

            UpdateThumbnailTitle();
            RefreshThumbnailVisibility();
        }

        public void SetThumbnailInteractionEnabled(bool enabled)
        {
            ThumbnailList.IsEnabled = enabled;
        }

        private void RefreshThumbnailVisibility()
        {
            var count = _thumbnailItems.Length;

            var show = count > 0 && (_thumbnailMode == ThumbnailDisplayMode.Shown || (_thumbnailMode == ThumbnailDisplayMode.Auto && count > 1));
            var next = show ? Visibility.Visible : Visibility.Collapsed;
            if (ThumbnailPanel.Visibility == next) return;

            ThumbnailPanel.Visibility = next;
            //等布局高度更新后，让主图重新适应窗口
            if (HasImage)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Fit));
            }

        }

        private void ReopenThumbnailPanel()
        {
            if (_thumbnailItems.Length == 0) return;
            _thumbnailMode = ThumbnailDisplayMode.Shown;
            RefreshThumbnailVisibility();
        }

        private void OnHideThumbnails(object sender,RoutedEventArgs e)
        {
            _thumbnailMode = ThumbnailDisplayMode.Hidden;
            RefreshThumbnailVisibility();

            e.Handled = true;
        }

        private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(_syscingThumbnails||!ThumbnailList.IsEnabled||ThumbnailList.SelectedItem is not ThumbnailItem item)
            {
                return;
            }
            //切换图像控件的预览
            ImageSelectionRequested?.Invoke(_thumbnailOperationId, item.FilePath);
            UpdateThumbnailTitle();
            e.Handled = true;
        }

        private void UpdateThumbnailTitle(bool preview = false)
        {
            var current = ThumbnailList.SelectedIndex + 1;
            var count = _thumbnailItems.Length;

            ThumbnailTitle.Text = $"图片源({current}/{count}）" + (preview ? "预览" : "");
        }






    }
}

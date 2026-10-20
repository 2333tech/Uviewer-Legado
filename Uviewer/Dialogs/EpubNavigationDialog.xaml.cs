using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services;

namespace Uviewer.Dialogs
{
    public sealed partial class EpubNavigationDialog : ContentDialog
    {
        private readonly EpubNavigationQueue _navigationQueue;
        private readonly int _chapterCount;
        private bool _isSynchronizing = true;
        private bool _isClosed;
        private long _inputRevision;

        public Task PendingNavigation => _navigationQueue.Completion;

        public EpubNavigationDialog(
            EpubNavigationPosition position,
            int chapterCount,
            Func<int, int, Task<EpubNavigationPosition>> navigateAsync)
        {
            _navigationQueue = new EpubNavigationQueue(
                navigateAsync,
                (selected, revision) =>
                {
                    if (!_isClosed && revision == _inputRevision) ApplyPosition(selected);
                },
                SetLoading,
                ShowNavigationError);
            _chapterCount = Math.Max(1, chapterCount);
            InitializeComponent();
            ChapterInput.Maximum = _chapterCount;
            ChapterSlider.Maximum = _chapterCount;
            ChapterTotalText.Text = $"/ {_chapterCount}";
            ApplyPosition(position);
            AutomationProperties.SetName(ChapterInput, Strings.ChapterLabel);
            AutomationProperties.SetName(ChapterSlider, Strings.ChapterLabel);
            AutomationProperties.SetName(LineInput, Strings.LineLabel);
            AutomationProperties.SetName(LineSlider, Strings.LineLabel);
            RegisterSliderRelease(ChapterSlider, () => RequestNavigation((int)ChapterSlider.Value, 1));
            RegisterSliderRelease(LineSlider, () => RequestNavigation((int)ChapterInput.Value, (int)LineSlider.Value));
            RegisterInputEditing(ChapterInput);
            RegisterInputEditing(LineInput);
            Opened += (_, _) => RequestNavigation((int)ChapterInput.Value, (int)LineInput.Value);
            Closed += (_, _) => _isClosed = true;
            PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                RequestNavigation((int)ChapterInput.Value, (int)LineInput.Value);
            };
        }

        private void RegisterInputEditing(NumberBox input)
        {
            // Do not overwrite text the user starts editing while a load finishes.
            input.PreviewKeyDown += (_, _) => _inputRevision++;
            input.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _inputRevision++), true);
        }

        private void RegisterSliderRelease(Slider slider, Action commit)
        {
            uint? pointerId = null;
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (args.GetCurrentPoint(slider).Properties.IsLeftButtonPressed ||
                    args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                {
                    _inputRevision++;
                    pointerId ??= args.Pointer.PointerId;
                }
            }), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
            {
                if (pointerId != args.Pointer.PointerId) return;
                pointerId = null;
                commit();
            }), true);
            slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, args) =>
            {
                if (pointerId == args.Pointer.PointerId) pointerId = null;
            }), true);
        }

        private void ChapterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
            SynchronizeValue(ChapterInput, ChapterSlider, e.NewValue);

        private void LineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
            SynchronizeValue(LineInput, LineSlider, e.NewValue);

        private void ChapterInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isSynchronizing) return;
            if (!double.IsFinite(args.NewValue))
            {
                IsPrimaryButtonEnabled = false;
                return;
            }
            SynchronizeValue(ChapterInput, ChapterSlider, args.NewValue);
            RequestNavigation((int)ChapterInput.Value, 1);
        }

        private void LineInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isSynchronizing) return;
            if (!double.IsFinite(args.NewValue))
            {
                IsPrimaryButtonEnabled = false;
                return;
            }
            SynchronizeValue(LineInput, LineSlider, args.NewValue);
            RequestNavigation((int)ChapterInput.Value, (int)LineInput.Value);
        }

        private void SynchronizeValue(NumberBox input, Slider slider, double value)
        {
            if (_isSynchronizing) return;
            _isSynchronizing = true;
            try
            {
                value = Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);
                input.Value = value;
                slider.Value = value;
                _inputRevision++;
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void ApplyPosition(EpubNavigationPosition position)
        {
            _isSynchronizing = true;
            try
            {
                ChapterInput.Value = ChapterSlider.Value = Math.Clamp(position.Chapter, 1, _chapterCount);
                int totalLines = Math.Max(1, position.TotalLines);
                // Reduce the value before lowering either control's upper bound.
                LineInput.Value = LineSlider.Value = 1;
                LineInput.Maximum = LineSlider.Maximum = totalLines;
                LineInput.Value = LineSlider.Value = Math.Clamp(position.Line, 1, totalLines);
                LineTotalText.Text = $"/ {totalLines}";
                ChapterSlider.IsEnabled = _chapterCount > 1;
                LineSlider.IsEnabled = totalLines > 1;
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void RequestNavigation(int chapter, int line)
        {
            if (_isClosed || chapter < 1 || chapter > _chapterCount || line < 1) return;
            _navigationQueue.Enqueue(chapter, line, _inputRevision);
        }

        private void SetLoading(bool isLoading)
        {
            if (_isClosed) return;
            LoadingIndicator.IsActive = isLoading;
            LoadingIndicator.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            if (isLoading) ErrorText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = double.IsFinite(ChapterInput.Value) && double.IsFinite(LineInput.Value);
        }

        private void ShowNavigationError(Exception ex)
        {
            if (_isClosed) return;
            if (ex is OperationCanceledException)
            {
                Hide();
                return;
            }
            StartupDiagnostics.Record("Navigating EPUB chapter/line", ex);
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}

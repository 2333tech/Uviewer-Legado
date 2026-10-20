using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;

namespace Uviewer.Dialogs
{
    public sealed partial class GoToLineDialog : ContentDialog
    {
        public string EnteredText => InputBox.Text;
        public string DialogTitle { get; }
        public string Placeholder { get; }
        public string CurrentLineText { get; set; }
        public string TotalLinesText { get; }
        public event EventHandler<int>? SliderPositionSelected;

        private readonly int _totalLines;
        private bool _isSynchronizing = true;
        private uint? _sliderPointerId;

        public GoToLineDialog(int currentLine, int totalLines, string title)
        {
            _totalLines = Math.Max(1, totalLines);
            DialogTitle = title;
            Placeholder = $"1 - {_totalLines}";
            TotalLinesText = _totalLines.ToString();
            CurrentLineText = Math.Clamp(currentLine, 1, _totalLines).ToString();

            this.InitializeComponent();
            PositionSlider.Maximum = _totalLines;
            PositionSlider.Value = Math.Clamp(currentLine, 1, _totalLines);
            PositionSlider.IsEnabled = _totalLines > 1;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PositionSlider, title);
            // Slider handles pointer events internally; also receive handled events
            // so releasing its thumb commits the selected position.
            PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PositionSlider_PointerPressed), true);
            PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PositionSlider_PointerReleased), true);
            PositionSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PositionSlider_PointerCanceled), true);
            _isSynchronizing = false;
            InputBox.SelectAll();
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSynchronizing) return;

            bool isValid = int.TryParse(InputBox.Text, out int line) && line >= 1 && line <= _totalLines;
            IsPrimaryButtonEnabled = isValid;
            if (!isValid) return;

            _isSynchronizing = true;
            try
            {
                PositionSlider.Value = line;
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isSynchronizing) return;

            UpdateSelectedPosition(e.NewValue);
        }

        private void UpdateSelectedPosition(double value)
        {
            _isSynchronizing = true;
            try
            {
                CurrentLineText = ((int)Math.Round(value)).ToString();
                InputBox.Text = CurrentLineText;
                IsPrimaryButtonEnabled = true;
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(PositionSlider).Properties.IsLeftButtonPressed ||
                e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            {
                _sliderPointerId ??= e.Pointer.PointerId;
            }
        }

        private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_sliderPointerId != e.Pointer.PointerId) return;

            _sliderPointerId = null;
            UpdateSelectedPosition(PositionSlider.Value);
            SliderPositionSelected?.Invoke(this, (int)Math.Round(PositionSlider.Value));
        }

        private void PositionSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_sliderPointerId == e.Pointer.PointerId)
            {
                _sliderPointerId = null;
            }
        }
    }
}

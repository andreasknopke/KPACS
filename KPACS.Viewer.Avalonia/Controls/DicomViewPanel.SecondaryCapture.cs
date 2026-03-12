using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    private bool _showKeyImageButton;
    private bool _hasSecondaryCapture;
    private bool _secondaryCaptureEnabled = true;

    public sealed record SecondaryCaptureSnapshot(byte[] RgbPixels, int Width, int Height);

    public event Action? SecondaryCaptureToggleRequested;

    public void SetSecondaryCaptureState(bool isVisible, bool isActive, bool isEnabled)
    {
        _showKeyImageButton = isVisible;
        _hasSecondaryCapture = isActive;
        _secondaryCaptureEnabled = isEnabled;
        UpdateSecondaryCaptureButton();
    }

    public SecondaryCaptureSnapshot? CaptureSecondaryCaptureSnapshot()
    {
        if (!IsImageLoaded)
        {
            return null;
        }

        int width = Math.Max(1, (int)Math.Ceiling(RootGrid.Bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(RootGrid.Bounds.Height));
        bool previousVisibility = KeyImageButton.IsVisible;

        KeyImageButton.IsVisible = false;
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(RootGrid);

            int stride = width * 4;
            byte[] bgra = new byte[stride * height];
            IntPtr buffer = Marshal.AllocHGlobal(bgra.Length);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bgra.Length, stride);
                Marshal.Copy(buffer, bgra, 0, bgra.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            byte[] rgb = new byte[width * height * 3];
            int targetIndex = 0;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int sourceIndex = rowOffset + (x * 4);
                    rgb[targetIndex++] = bgra[sourceIndex + 2];
                    rgb[targetIndex++] = bgra[sourceIndex + 1];
                    rgb[targetIndex++] = bgra[sourceIndex];
                }
            }

            return new SecondaryCaptureSnapshot(rgb, width, height);
        }
        finally
        {
            KeyImageButton.IsVisible = previousVisibility;
            UpdateSecondaryCaptureButton();
        }
    }

    private void UpdateSecondaryCaptureButton()
    {
        if (KeyImageButton is null)
        {
            return;
        }

        KeyImageButton.IsVisible = _showKeyImageButton && IsImageLoaded;
        KeyImageButton.IsEnabled = _secondaryCaptureEnabled;
        KeyImageButton.Background = _hasSecondaryCapture
            ? new SolidColorBrush(Color.Parse("#FFF6D04D"))
            : new SolidColorBrush(Color.Parse("#B0202020"));
        KeyImageButton.BorderBrush = _hasSecondaryCapture
            ? new SolidColorBrush(Color.Parse("#FFFFF4B3"))
            : new SolidColorBrush(Color.Parse("#80909090"));
        KeyImageButton.Foreground = _hasSecondaryCapture
            ? Brushes.Black
            : new SolidColorBrush(Color.Parse("#FFF6D04D"));
        KeyImageButton.Opacity = _secondaryCaptureEnabled ? 1.0 : 0.5;
        ToolTip.SetTip(KeyImageButton,
            _hasSecondaryCapture ? "Delete key image" : "Create key image");
    }

    private void OnKeyImageButtonClick(object? sender, RoutedEventArgs e)
    {
        SecondaryCaptureToggleRequested?.Invoke();
        e.Handled = true;
    }
}
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Interactivity;
using System.Runtime.InteropServices;
using Avalonia.Platform.Storage;

namespace Doodly;

public partial class MainWindow : Window
{
    private WriteableBitmap? _canvasBitmap;
    private bool _isPointerDown;
    private bool _isPanning;
    private Point _panStartPosition;
    private Vector _panStartOffset;
    private readonly List<WriteableBitmap> _changeHistory = [];
    private readonly List<WriteableBitmap> _reverseHistory = [];

    public MainWindow()
    {
        InitializeComponent();
        ZoomNumericUpDown.PropertyChanged += (_, args) =>
        {
            if (args.Property == NumericUpDown.ValueProperty)
                UpdateCanvasZoom();
        };
        LoadPlaceholder();
    }

    private void LoadPlaceholder()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Doodly/placeholder.png"));
        using var source = new Bitmap(stream);
        _canvasBitmap = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);

        using (var framebuffer = _canvasBitmap.Lock())
            source.CopyPixels(framebuffer);

        CanvasImage.Source = _canvasBitmap;
        UpdateCanvasZoom();
    }

    public void AccessCanvasBitmap(Action<ILockedFramebuffer> access)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (_canvasBitmap is null)
            throw new InvalidOperationException("The canvas bitmap has not been loaded.");

        using var framebuffer = _canvasBitmap.Lock();
        access(framebuffer);
    }

    private PixelPoint? GetPixelUnderCursor(Point position)
    {
        if (CanvasImage.Source is not Bitmap bitmap ||
            CanvasImage.Bounds.Width <= 0 ||
            CanvasImage.Bounds.Height <= 0)
            return null;

        var pixelSize = bitmap.PixelSize;
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return null;

        if (position.X < 0 ||
            position.Y < 0 ||
            position.X >= CanvasImage.Bounds.Width ||
            position.Y >= CanvasImage.Bounds.Height)
            return null;

        return new PixelPoint(
            (int)(position.X / CanvasImage.Bounds.Width * pixelSize.Width),
            (int)(position.Y / CanvasImage.Bounds.Height * pixelSize.Height));
    }

    private void UpdateCanvasZoom()
    {
        if (_canvasBitmap is null)
            return;

        var zoom = (double)(ZoomNumericUpDown.Value ?? 100m) / 100;
        CanvasImage.Width = _canvasBitmap.Size.Width * zoom;
        CanvasImage.Height = _canvasBitmap.Size.Height * zoom;

        CanvasScrollViewer.UpdateLayout();
        CanvasScrollViewer.Offset = new Vector(
            Math.Max(0, (CanvasImage.Bounds.Width - CanvasScrollViewer.Viewport.Width) / 2),
            Math.Max(0, (CanvasImage.Bounds.Height - CanvasScrollViewer.Viewport.Height) / 2));
    }

    private void CanvasImage_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(CanvasImage).Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPosition = e.GetPosition(this);
            _panStartOffset = CanvasScrollViewer.Offset;
            CanvasImage.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(CanvasImage);
            e.Handled = true;
            return;
        }

        _isPointerDown = true;
        e.Pointer.Capture(CanvasImage);
        if (_canvasBitmap is not null &&
            (EraseToggle.IsChecked is true || DrawToggle.IsChecked is true || FillToggle.IsChecked is true) &&
            GetPixelUnderCursor(e.GetPosition(CanvasImage)) is not null)
        {
            _changeHistory.Add(CloneCanvasBitmap(_canvasBitmap));
            _reverseHistory.Clear();
        }

        ApplyToolAt(e.GetPosition(CanvasImage));
    }

    private static WriteableBitmap CloneCanvasBitmap(Bitmap source)
    {
        var clone = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);

        using var framebuffer = clone.Lock();
        source.CopyPixels(framebuffer);
        return clone;
    }

    private void CanvasImage_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var delta = e.GetPosition(this) - _panStartPosition;
            CanvasScrollViewer.Offset = new Vector(
                _panStartOffset.X - delta.X,
                _panStartOffset.Y - delta.Y);
            return;
        }

        if (_isPointerDown)
            ApplyToolAt(e.GetPosition(CanvasImage));
    }

    private void CanvasImage_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            UpdateCanvasCursor();
        }

        _isPointerDown = false;
        e.Pointer.Capture(null);
    }

    private void ApplyToolAt(Point position)
    {
        var pixel = GetPixelUnderCursor(position);
        if (_canvasBitmap is null)
            return;
        if (pixel is null)
            return;
        
        var pixels = new List<PixelPoint>(GetBrushPixels(pixel.Value, (int)Math.Round(SizeSlider.Value)));
        var bitmapChanged = false;
        using (ILockedFramebuffer framebuffer = _canvasBitmap.Lock())
        {
            foreach (PixelPoint point in pixels)
            {
                if (EraseToggle.IsChecked is true)
                {
                    EraseAt(point, framebuffer);
                    bitmapChanged = true;
                }
                else if (DrawToggle.IsChecked is true)
                {
                    DrawAt(point, framebuffer);
                    bitmapChanged = true;
                }
            }

            if (PickerToggle.IsChecked is true)
                PickAt(pixel.Value, framebuffer);
            else if (FillToggle.IsChecked is true)
                bitmapChanged = FillAt(pixel.Value, framebuffer);
        }

        if (bitmapChanged)
            CanvasImage.InvalidateVisual();
    }

    private void EraseAt(PixelPoint pixel, ILockedFramebuffer framebuffer)
    {
        Marshal.WriteInt32(
            framebuffer.Address, 
            pixel.Y * framebuffer.RowBytes + pixel.X * 4,
            0);
    }

    private void DrawAt(PixelPoint pixel, ILockedFramebuffer framebuffer)
    {
        var offset = pixel.Y * framebuffer.RowBytes + pixel.X * 4;
        var color = SelectedColorPicker.Color;
        var pixelColor = ReadColor(pixel, framebuffer);
        var strength = Math.Clamp(BrushStrengthSlider.Value / 100, 0, 1);
        var newColor = Color.FromArgb(
            BlendChannel(pixelColor.A, color.A, strength),
            BlendChannel(pixelColor.R, color.R, strength),
            BlendChannel(pixelColor.G, color.G, strength),
            BlendChannel(pixelColor.B, color.B, strength));

        Marshal.WriteByte(framebuffer.Address, offset, newColor.B);
        Marshal.WriteByte(framebuffer.Address, offset + 1, newColor.G);
        Marshal.WriteByte(framebuffer.Address, offset + 2, newColor.R);
        Marshal.WriteByte(framebuffer.Address, offset + 3, newColor.A);
    }

    private static byte BlendChannel(byte original, byte selected, double strength)
    {
        return (byte)Math.Round(original + (selected - original) * strength);
    }

    private void PickAt(PixelPoint pixel, ILockedFramebuffer framebuffer)
    {
        SelectedColorPicker.Color = ReadColor(pixel, framebuffer);
    }

    private static Color ReadColor(PixelPoint pixel, ILockedFramebuffer framebuffer)
    {
        var offset = pixel.Y * framebuffer.RowBytes + pixel.X * 4;
        var blue = Marshal.ReadByte(framebuffer.Address, offset);
        var green = Marshal.ReadByte(framebuffer.Address, offset + 1);
        var red = Marshal.ReadByte(framebuffer.Address, offset + 2);
        var alpha = Marshal.ReadByte(framebuffer.Address, offset + 3);

        return Color.FromArgb(alpha, red, green, blue);
    }

    private bool FillAt(PixelPoint start, ILockedFramebuffer framebuffer)
    {
        int width = framebuffer.Size.Width;
        int height = framebuffer.Size.Height;
        List<PixelPoint> collected = [];
        if (start.X < 0 || start.Y < 0 || start.X >= width || start.Y >= height)
            return false;

        Color targetColor = ReadColor(start, framebuffer);
        Color fillColor = SelectedColorPicker.Color;
        if (targetColor == fillColor)
            return false;

        Queue<PixelPoint> queue = new Queue<PixelPoint>();
        HashSet<long> visited = [];
        queue.Enqueue(start);
        visited.Add((long)start.Y * width + start.X);

        while (queue.Count > 0)
        {
            var pixel = queue.Dequeue();
            collected.Add(pixel);

            AddMatchingNeighbor(pixel.X - 1, pixel.Y);
            AddMatchingNeighbor(pixel.X + 1, pixel.Y);
            AddMatchingNeighbor(pixel.X, pixel.Y - 1);
            AddMatchingNeighbor(pixel.X, pixel.Y + 1);
        }

        foreach (PixelPoint pixelPoint in collected)
        {
            DrawAt(pixelPoint, framebuffer);
        }

        return collected.Count > 0;

        void AddMatchingNeighbor(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            var key = (long)y * width + x;
            if (!visited.Add(key))
                return;

            if (ReadColor(new PixelPoint(x, y), framebuffer) == targetColor)
            {
                queue.Enqueue(new PixelPoint(x, y));
            }
        }
    }

    private IEnumerable<PixelPoint> GetBrushPixels(PixelPoint center, int diameter)
    {
        if (_canvasBitmap is null || diameter <= 0)
            yield break;

        var radius = diameter / 2;
        var radiusSquared = radius * radius;
        var bounds = _canvasBitmap.PixelSize;

        for (var y = Math.Max(0, center.Y - radius);
             y <= Math.Min(bounds.Height - 1, center.Y + radius);
             y++)
        {
            for (var x = Math.Max(0, center.X - radius);
                 x <= Math.Min(bounds.Width - 1, center.X + radius);
                 x++)
            {
                var dx = x - center.X;
                var dy = y - center.Y;
                if (dx * dx + dy * dy <= radiusSquared)
                    yield return new PixelPoint(x, y);
            }
        }
    }

    private void EraseToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EraseToggle.IsChecked is true)
        {
            DrawToggle.IsChecked = false;
            PickerToggle.IsChecked = false;
            FillToggle.IsChecked = false;
        }

        UpdateCanvasCursor();
    }

    private void DrawToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DrawToggle.IsChecked is true)
        {
            EraseToggle.IsChecked = false;
            PickerToggle.IsChecked = false;
            FillToggle.IsChecked = false;
        }

        UpdateCanvasCursor();
    }

    private void PickerToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PickerToggle.IsChecked is true)
        {
            EraseToggle.IsChecked = false;
            DrawToggle.IsChecked = false;
            FillToggle.IsChecked = false;
        }

        UpdateCanvasCursor();
    }

    private void FillToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (FillToggle.IsChecked is true)
        {
            EraseToggle.IsChecked = false;
            DrawToggle.IsChecked = false;
            PickerToggle.IsChecked = false;
        }

        UpdateCanvasCursor();
    }

    private void UpdateCanvasCursor()
    {
        if (EraseToggle.IsChecked is true) CanvasImage.Cursor = new Cursor(StandardCursorType.No);
        else if (DrawToggle.IsChecked is true) CanvasImage.Cursor = new Cursor(StandardCursorType.Cross);
        else if (PickerToggle.IsChecked is true) CanvasImage.Cursor = new Cursor(StandardCursorType.Help);
        else if (FillToggle.IsChecked is true) CanvasImage.Cursor = new Cursor(StandardCursorType.Cross);
        else CanvasImage.Cursor = null;
    }

    private async void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? topLevel = GetTopLevel(this);
        if (topLevel is null || _canvasBitmap is null)
            return;
        
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image",
            SuggestedFileName = "Doodly.png",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG image")
                {
                    Patterns = ["*.png"],
                    MimeTypes = ["image/png"]
                }
            ]
        });
        if (file is null)
            return;
        
        await using var stream = await file.OpenWriteAsync();
        _canvasBitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private async void LoadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? topLevel = GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image files")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"],
                    MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/gif"]
                }
            ]
        });
        if (files.Count < 1)
            return;

        await using var stream = await files[0].OpenReadAsync();
        LoadImage(stream);
    }

    public void OpenImage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = File.OpenRead(filePath);
        LoadImage(stream);
    }

    private void LoadImage(Stream stream)
    {
        using var source = new Bitmap(stream);
        var bitmap = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);

        using (var framebuffer = bitmap.Lock())
            source.CopyPixels(framebuffer);

        _canvasBitmap = bitmap;
        _changeHistory.Clear();
        _reverseHistory.Clear();
        CanvasImage.Source = _canvasBitmap;
        UpdateCanvasZoom();
    }

    private void UndoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_changeHistory.Count == 0 || _canvasBitmap is null) return;
        _reverseHistory.Add(_canvasBitmap);
        var lastIndex = _changeHistory.Count - 1;
        _canvasBitmap = _changeHistory[lastIndex];
        _changeHistory.RemoveAt(lastIndex);
        CanvasImage.Source = _canvasBitmap;
        UpdateCanvasZoom();
    }

    private void RedoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_reverseHistory.Count == 0 || _canvasBitmap is null) return;
        _changeHistory.Add(_canvasBitmap);
        var lastIndex = _reverseHistory.Count - 1;
        _canvasBitmap = _reverseHistory[lastIndex];
        _reverseHistory.RemoveAt(lastIndex);
        CanvasImage.Source = _canvasBitmap;
        UpdateCanvasZoom();
    }
}
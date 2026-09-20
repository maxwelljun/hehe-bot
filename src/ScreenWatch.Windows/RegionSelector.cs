using ScreenWatch.Core;

namespace ScreenWatch.Windows;

internal static class RegionSelector
{
    public static async Task<CaptureRegion?> SelectAsync()
    {
        var completion = new TaskCompletionSource<CaptureRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<SelectionOverlay>();
        try
        {
            // Capture all screens before displaying any overlay, so overlays never appear in snapshots.
            foreach (var screen in Screen.AllScreens)
                overlays.Add(new SelectionOverlay(screen.Bounds, region => completion.TrySetResult(region)));
            foreach (var overlay in overlays) overlay.Show();
            overlays.FirstOrDefault(overlay => overlay.Bounds.Contains(Cursor.Position))?.Activate();
            return await completion.Task;
        }
        finally
        {
            foreach (var overlay in overlays) overlay.Dispose();
        }
    }

    private sealed class SelectionOverlay : Form
    {
        private readonly Rectangle _screenBounds;
        private readonly Bitmap _snapshot;
        private readonly Action<CaptureRegion?> _complete;
        private Point? _anchor;
        private Rectangle _selection;

        public SelectionOverlay(Rectangle screenBounds, Action<CaptureRegion?> complete)
        {
            _screenBounds = screenBounds;
            _complete = complete;
            _snapshot = ScreenCapture.Capture(new(screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = screenBounds;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = _screenBounds;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImage(_snapshot, ClientRectangle);
            using var shade = new SolidBrush(Color.FromArgb(155, 9, 18, 36));
            e.Graphics.FillRectangle(shade, ClientRectangle);
            if (!_selection.IsEmpty)
            {
                var source = ToScreenRegion(_selection);
                e.Graphics.DrawImage(_snapshot, _selection,
                    new Rectangle(source.X - _screenBounds.X, source.Y - _screenBounds.Y, source.Width, source.Height), GraphicsUnit.Pixel);
                using var pen = new Pen(Color.FromArgb(63, 165, 255), 2);
                e.Graphics.DrawRectangle(pen, _selection);
            }
            string hint = _selection.IsEmpty ? "拖动框选监控区域 · Esc / 右键取消 · 请在单块显示器内选择"
                : $"{ToScreenRegion(_selection).Width} × {ToScreenRegion(_selection).Height} 像素 · 松开鼠标确认";
            TextRenderer.DrawText(e.Graphics, hint, SystemFonts.MessageBoxFont,
                new Rectangle(24, 24, Math.Max(1, ClientSize.Width - 48), 50), Color.White, Color.FromArgb(22, 38, 62),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { _complete(null); return; }
            if (e.Button != MouseButtons.Left) return;
            _anchor = e.Location;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_anchor is not { } start) return;
            var end = new Point(Math.Clamp(e.X, 0, ClientSize.Width), Math.Clamp(e.Y, 0, ClientSize.Height));
            _selection = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _anchor is null) return;
            OnMouseMove(e);
            _anchor = null;
            Capture = false;
            var region = ToScreenRegion(_selection);
            if (region.IsValid) _complete(region);
            else { _selection = Rectangle.Empty; Invalidate(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) _complete(null);
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _complete(null);
            base.OnFormClosed(e);
        }

        private CaptureRegion ToScreenRegion(Rectangle rectangle)
        {
            double scaleX = (double)_snapshot.Width / Math.Max(1, ClientSize.Width);
            double scaleY = (double)_snapshot.Height / Math.Max(1, ClientSize.Height);
            return new(_screenBounds.X + (int)Math.Round(rectangle.X * scaleX),
                _screenBounds.Y + (int)Math.Round(rectangle.Y * scaleY),
                (int)Math.Round(rectangle.Width * scaleX), (int)Math.Round(rectangle.Height * scaleY));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _snapshot.Dispose();
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class DarkScroll
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        internal int Size, Mask, Minimum, Maximum, Page, Position, Track;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);

    internal static void Paint(Control control, int message)
    {
        if (!control.IsHandleCreated ||
            (message != 0x0085 && message != 0x000F && message != 0x0114 && message != 0x0115 &&
             message != 0x020A && message != 0x0100 && message != 0x0005)) return;

        int side = control.Width - control.ClientSize.Width;
        int bottom = control.Height - control.ClientSize.Height;
        if (side <= 1 && bottom <= 1) return;
        IntPtr dc = GetWindowDC(control.Handle);
        if (dc == IntPtr.Zero) return;
        try
        {
            using (var graphics = Graphics.FromHdc(dc))
            using (var track = new SolidBrush(Color.FromArgb(24, 24, 24)))
            using (var thumb = new SolidBrush(Color.FromArgb(100, 100, 100)))
            {
                if (side > 1)
                    Draw(graphics, control.Handle, 1, new Rectangle(control.Width - side, 0, side, control.Height - bottom), track, thumb);
                if (bottom > 1)
                    Draw(graphics, control.Handle, 0, new Rectangle(0, control.Height - bottom, control.Width - side, bottom), track, thumb);
                if (side > 1 && bottom > 1)
                    graphics.FillRectangle(track, control.Width - side, control.Height - bottom, side, bottom);
            }
        }
        finally { ReleaseDC(control.Handle, dc); }
    }

    private static void Draw(Graphics graphics, IntPtr window, int bar, Rectangle bounds, Brush track, Brush thumb)
    {
        graphics.FillRectangle(track, bounds);
        var info = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), Mask = 7 };
        if (!GetScrollInfo(window, bar, ref info)) return;

        bool vertical = bar == 1;
        int button = vertical ? bounds.Width : bounds.Height;
        int length = (vertical ? bounds.Height : bounds.Width) - 2 * button;
        if (length <= 0) return;
        long total = Math.Max(1L, (long)info.Maximum - info.Minimum + 1L);
        int handle = Math.Min(length, Math.Max(12, (int)(length * (long)Math.Max(1, info.Page) / total)));
        int offset = (int)((length - handle) * (info.Position - (long)info.Minimum) / Math.Max(1L, total - info.Page));
        offset = Math.Min(length - handle, Math.Max(0, offset)) + button;
        var marker = vertical
            ? new Rectangle(bounds.X + 4, bounds.Y + offset, Math.Max(2, button - 8), handle)
            : new Rectangle(bounds.X + offset, bounds.Y + 4, handle, Math.Max(2, button - 8));
        graphics.FillRectangle(thumb, marker);
        DrawArrow(graphics, thumb, bounds, button, vertical, false);
        DrawArrow(graphics, thumb, bounds, button, vertical, true);
    }

    private static void DrawArrow(Graphics graphics, Brush brush, Rectangle bounds, int size, bool vertical, bool end)
    {
        int x = vertical ? bounds.Left + size / 2 : end ? bounds.Right - size / 2 : bounds.Left + size / 2;
        int y = vertical ? end ? bounds.Bottom - size / 2 : bounds.Top + size / 2 : bounds.Top + size / 2;
        int direction = end ? 1 : -1;
        Point[] arrow = vertical
            ? new[] { new Point(x - 3, y - direction * 2), new Point(x + 3, y - direction * 2), new Point(x, y + direction * 2) }
            : new[] { new Point(x - direction * 2, y - 3), new Point(x - direction * 2, y + 3), new Point(x + direction * 2, y) };
        graphics.FillPolygon(brush, arrow);
    }
}

internal sealed class DarkScrollPage : FlowLayoutPanel
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        DarkScroll.Paint(this, message.Msg);
    }
}

internal sealed class DarkTextBox : TextBox
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        DarkScroll.Paint(this, message.Msg);
    }
}

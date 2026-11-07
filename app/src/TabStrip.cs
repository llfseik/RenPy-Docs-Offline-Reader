using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RenPyDocs
{
    // One entry in the strip. The shell owns a single WebView2, so a tab is
    // just an address plus where the reader was when they left it.
    public class TabInfo
    {
        public string Url = "";
        public string Title = "";
        public double Y;
        public string YUrl = "";

        // pages visited inside this tab (the last one is what is on screen)
        // and the pages "forward" can reach again
        public readonly List<string> History = new List<string>();
        public readonly List<string> Forward = new List<string>();

        // the tab this one was opened from: back from here returns to it
        public TabInfo Opener;
    }

    // Browser-like tab strip drawn by WinForms. It lives in its own band above
    // the web view and never touches the page: the page keeps its own layout
    // whatever happens here.
    public class TabStrip : Control
    {
        public readonly List<TabInfo> Tabs = new List<TabInfo>();
        public int Active = -1;
        public bool Dark;

        public Action<int> SwitchTo;
        public Action NewTab;
        public Action<int> CloseTab;

        private const int Pad = 10;      // left / right padding of the band
        private const int Gap = 2;       // space between two tabs
        private const int Radius = 7;
        private const int MinW = 78;
        private const int MaxW = 220;
        private const int PlusW = 26;
        private const int CloseW = 18;

        private Font font;
        private Font fontActive;

        private int hoverTab = -1;
        private int hoverClose = -1;
        private bool hoverPlus;

        public TabStrip()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            fontActive = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point);
            Height = font.Height + 19;
        }

        // ------------------------------------------------------------ colours
        private Color BarColor
        {
            get { return Dark ? Color.FromArgb(11, 12, 13) : Color.FromArgb(233, 237, 241); }
        }

        private Color BorderColor
        {
            get { return Dark ? Color.FromArgb(36, 36, 36) : Color.FromArgb(221, 227, 232); }
        }

        private Color ActiveColor
        {
            get { return Dark ? Color.FromArgb(20, 20, 20) : Color.FromArgb(252, 252, 252); }
        }

        private Color HoverColor
        {
            get { return Dark ? Color.FromArgb(23, 25, 28) : Color.FromArgb(242, 245, 247); }
        }

        private Color TextColor
        {
            get { return Dark ? Color.FromArgb(154, 164, 174) : Color.FromArgb(92, 104, 115); }
        }

        private Color ActiveTextColor
        {
            get { return Dark ? Color.FromArgb(228, 232, 236) : Color.FromArgb(34, 48, 60); }
        }

        private Color GlyphColor
        {
            get { return Dark ? Color.FromArgb(125, 136, 148) : Color.FromArgb(142, 152, 162); }
        }

        private Color GlyphHot
        {
            get { return Dark ? Color.FromArgb(255, 139, 122) : Color.FromArgb(192, 57, 43); }
        }

        // ------------------------------------------------------------- layout
        private int TabHeight
        {
            get { return Math.Max(24, Height - 4); }
        }

        private static Rectangle CloseRect(Rectangle tab)
        {
            return new Rectangle(tab.Right - CloseW - 3, tab.Y + (tab.Height - 16) / 2, CloseW - 2, 16);
        }

        private List<Rectangle> ComputeLayout(out Rectangle plus)
        {
            List<Rectangle> rects = new List<Rectangle>();
            int n = Tabs.Count;
            int tabH = TabHeight;
            int top = Height - tabH;

            plus = new Rectangle(Pad, top, PlusW, tabH);
            if (n == 0) return rects;

            int avail = Width - Pad * 2 - PlusW;
            int[] want = new int[n];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                string text = Tabs[i].Title;
                if (text == null) text = "";
                int tw = TextRenderer.MeasureText(text, font,
                    new Size(int.MaxValue, tabH),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                want[i] = Math.Max(MinW, Math.Min(MaxW, tw + 12 + 6 + CloseW + 6));
                total += want[i] + (i > 0 ? Gap : 0);
            }

            double scale = 1.0;
            if (total > avail && total > 0) scale = (double)avail / total;

            int[] w = new int[n];
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                w[i] = Math.Max(64, (int)Math.Round(want[i] * scale));
                used += w[i] + (i > 0 ? Gap : 0);
            }

            // too many tabs even after shrinking: slide the band so the active
            // one stays fully visible
            int offset = 0;
            int limit = Width - Pad - PlusW;
            if (used > avail && used > 0)
            {
                int a = Active;
                if (a < 0 || a >= n) a = 0;
                int left = Pad;
                for (int i = 0; i < a; i++) left += w[i] + Gap;
                int right = left + w[a];
                if (right > limit) offset = right - limit;
                if (left - offset < Pad) offset = left - Pad;
                if (offset < 0) offset = 0;
            }

            int x = Pad - offset;
            for (int i = 0; i < n; i++)
            {
                rects.Add(new Rectangle(x, top, w[i], tabH));
                x += w[i] + Gap;
            }
            plus = new Rectangle(Math.Min(x + 2, Width - Pad - PlusW), top, PlusW, tabH);
            return rects;
        }

        private static GraphicsPath TabPath(Rectangle r)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Radius * 2;
            if (r.Width < d + 2) d = Math.Max(2, r.Width - 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.AddLine(r.X, r.Bottom, r.X, r.Y + d / 2);
            p.CloseFigure();
            return p;
        }

        // -------------------------------------------------------------- paint
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush b = new SolidBrush(BarColor))
                g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(BorderColor))
                g.DrawLine(p, 0, Height - 1, Width - 1, Height - 1);

            Rectangle plus;
            List<Rectangle> rects = ComputeLayout(out plus);

            for (int i = 0; i < rects.Count; i++)
            {
                Rectangle r = rects[i];
                bool active = i == Active;
                if (active || i == hoverTab)
                {
                    using (GraphicsPath path = TabPath(r))
                    using (SolidBrush b = new SolidBrush(active ? ActiveColor : HoverColor))
                        g.FillPath(b, path);
                }
                if (active)
                {
                    using (GraphicsPath path = TabPath(r))
                    using (Pen p = new Pen(BorderColor))
                        g.DrawPath(p, path);
                }

                Rectangle text = new Rectangle(r.X + 11, r.Y, r.Width - 11 - CloseW - 3, r.Height - 1);
                TextRenderer.DrawText(g, Tabs[i].Title ?? "", active ? fontActive : font, text,
                    active ? ActiveTextColor : TextColor,
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                if (active || i == hoverTab) DrawClose(g, CloseRect(r), i == hoverClose);
            }

            DrawPlus(g, plus);
        }

        private void DrawClose(Graphics g, Rectangle box, bool hot)
        {
            using (Pen p = new Pen(hot ? GlyphHot : GlyphColor, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cx = box.X + box.Width / 2;
                int cy = box.Y + box.Height / 2;
                g.DrawLine(p, cx - 3, cy - 3, cx + 3, cy + 3);
                g.DrawLine(p, cx + 3, cy - 3, cx - 3, cy + 3);
            }
        }

        private void DrawPlus(Graphics g, Rectangle r)
        {
            if (hoverPlus)
            {
                using (SolidBrush b = new SolidBrush(HoverColor))
                    g.FillRectangle(b, r);
            }
            using (Pen p = new Pen(hoverPlus ? GlyphHot : GlyphColor, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - 5, cy, cx + 5, cy);
                g.DrawLine(p, cx, cy - 5, cx, cy + 5);
            }
        }

        // -------------------------------------------------------------- mouse
        private int HitTest(Point p, out bool onClose, out bool onPlus)
        {
            onClose = false;
            onPlus = false;
            Rectangle plus;
            List<Rectangle> rects = ComputeLayout(out plus);
            if (plus.Contains(p))
            {
                onPlus = true;
                return -1;
            }
            for (int i = 0; i < rects.Count; i++)
            {
                if (!rects[i].Contains(p)) continue;
                onClose = CloseRect(rects[i]).Contains(p);
                return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool onClose, onPlus;
            int tab = HitTest(e.Location, out onClose, out onPlus);
            int close = onClose ? tab : -1;
            if (tab != hoverTab || close != hoverClose || onPlus != hoverPlus)
            {
                hoverTab = tab;
                hoverClose = close;
                hoverPlus = onPlus;
                Cursor = (onClose || onPlus) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverTab != -1 || hoverPlus)
            {
                hoverTab = -1;
                hoverClose = -1;
                hoverPlus = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            bool onClose, onPlus;
            int tab = HitTest(e.Location, out onClose, out onPlus);

            if (e.Button == MouseButtons.Left)
            {
                if (onPlus)
                {
                    if (NewTab != null) NewTab();
                }
                else if (tab >= 0)
                {
                    if (onClose)
                    {
                        if (CloseTab != null) CloseTab(tab);
                    }
                    else if (tab != Active)
                    {
                        if (SwitchTo != null) SwitchTo(tab);
                    }
                }
            }
            else if (e.Button == MouseButtons.Middle && tab >= 0 && !onClose)
            {
                if (CloseTab != null) CloseTab(tab);
            }
            base.OnMouseDown(e);
        }
    }
}

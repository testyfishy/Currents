// CurrentsTray - a notification-area control for the Currents service.
//
// WHAT THIS IS, AND IS NOT:
//   Currents itself is a Windows SERVICE. It runs 24/7 under its own account, with no desktop
//   session and no window, exactly like Jellyfin, Kavita and Syncthing on this host. That is the
//   point: the literature inbox must keep ingesting whether or not anyone is logged in, and must
//   be reachable from a phone at 3am.
//
//   So this program is NOT the application. It is a REMOTE CONTROL for it, in the same relation as
//   Syncthing Tray is to the Syncthing service. Quitting it stops nothing; it only removes the
//   icon. That is intended, and the menu item says so, because a "Quit" that silently left a
//   service running would be worse than no Quit at all.
//
//   There is correspondingly no window to minimise. The reading interface is a web page, so
//   closing that browser tab is the "minimise", and it costs nothing - state lives in the service.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.ServiceProcess;
using System.Text.Json.Serialization;

namespace CurrentsTray;

static class Program
{
    const string ServiceName = "Currents";
    const string LocalBase   = "http://127.0.0.1:8789";
    const string DataDir     = @"C:\Currents\data";
    const string LogDir      = @"C:\Currents\logs";

    // Read from the service rather than compiled in, so this binary carries no knowledge of any
    // particular tailnet. Falls back to loopback if the service has not answered yet.
    static string PublicUrl = LocalBase + "/";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var ctx = new TrayContext();
        Application.Run(ctx);
    }

    sealed class Stats
    {
        [JsonPropertyName("total")]   public long Total { get; set; }
        [JsonPropertyName("unread")]  public long Unread { get; set; }
        [JsonPropertyName("trials")]  public long Trials { get; set; }
        [JsonPropertyName("lastIngest")] public string? LastIngest { get; set; }
    }

    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly System.Windows.Forms.Timer _poll;
        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
        readonly ToolStripMenuItem _status, _unread, _fetch;
        Icon? _generated;

        public TrayContext()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };

            var open = new ToolStripMenuItem("Open Currents", null, (_, _) => OpenUi())
                       { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
            _unread = new ToolStripMenuItem("Unread: …") { Enabled = false };
            _status = new ToolStripMenuItem("Service: …") { Enabled = false };
            _fetch  = new ToolStripMenuItem("Fetch new papers now", null, async (_, _) => await FetchAsync());

            menu.Items.Add(open);
            menu.Items.Add(_unread);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_fetch);
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => Explore(DataDir)));
            menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => Explore(LogDir)));
            menu.Items.Add(new ToolStripSeparator());
            // The parenthetical is load-bearing. Without it a user reasonably assumes Quit stops
            // the service, and would wonder later why papers kept arriving.
            menu.Items.Add(new ToolStripMenuItem("Quit tray icon (service keeps running)", null, (_, _) => Quit()));

            _generated = BuildIcon(0);
            _icon = new NotifyIcon
            {
                Icon = _generated,
                Text = "Currents",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenUi(); };

            _poll = new System.Windows.Forms.Timer { Interval = 60_000 };
            _poll.Tick += async (_, _) => await RefreshAsync();
            _poll.Start();
            _ = RefreshAsync();
        }

        void OpenUi()
        {
            // The published URL, not localhost: it is the one that also works when the user later
            // opens the same link on a phone, and it exercises the real TLS path.
            try { Process.Start(new ProcessStartInfo(PublicUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Warn($"Could not open {PublicUrl}\n\n{ex.Message}"); }
        }

        static void Explore(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch { /* a missing folder is not worth a dialog */ }
        }

        async Task FetchAsync()
        {
            _fetch.Enabled = false;
            _fetch.Text = "Fetching…";
            try
            {
                using var resp = await _http.PostAsync($"{LocalBase}/api/ingest?days=14", null);
                resp.EnsureSuccessStatusCode();
                await RefreshAsync();
                _icon.ShowBalloonTip(4000, "Currents", "Fetch complete.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Warn($"Fetch failed.\n\n{ex.Message}"); }
            finally { _fetch.Enabled = true; _fetch.Text = "Fetch new papers now"; }
        }

        async Task RefreshAsync()
        {
            string svc;
            try
            {
                using var sc = new ServiceController(ServiceName);
                svc = sc.Status.ToString();
            }
            catch { svc = "not installed"; }
            _status.Text = $"Service: {svc}";

            try
            {
                // Pick up the published URL once the service is reachable.
                try
                {
                    using var sys = await _http.GetAsync($"{LocalBase}/api/system");
                    if (sys.IsSuccessStatusCode)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(await sys.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("publicUrl", out var pu) &&
                            pu.ValueKind == System.Text.Json.JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(pu.GetString()))
                            PublicUrl = pu.GetString()!;
                    }
                }
                catch { /* keep the loopback fallback */ }

                var s = await _http.GetFromJsonAsync<Stats>($"{LocalBase}/api/stats");
                if (s is null) throw new InvalidOperationException("empty response");
                _unread.Text = $"Unread: {s.Unread} of {s.Total},  {s.Trials} trials";
                // The tray tooltip is capped at 63 characters by the shell; longer text is silently
                // truncated rather than rejected, so keep it short on purpose.
                _icon.Text = Trim63($"Currents: {s.Unread} unread, {s.Trials} trials");
                SwapIcon(s.Unread);
            }
            catch
            {
                _unread.Text = "Unread: service not responding";
                _icon.Text = "Currents: not responding";
                SwapIcon(-1);
            }
        }

        static string Trim63(string s) => s.Length <= 63 ? s : s[..60] + "…";

        void SwapIcon(long unread)
        {
            var next = BuildIcon(unread);
            _icon.Icon = next;
            // NotifyIcon does not own the handle, so the previous one must be destroyed explicitly
            // or this leaks a GDI object every minute - about 1,440 a day, against a 10,000 quota.
            _generated?.Dispose();
            _generated = next;
        }

        // Drawn rather than shipped as an .ico so there is no binary asset to keep in sync: a book
        // spine with a dot that turns amber when something is unread, grey when the service is down.
        static Icon BuildIcon(long unread)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var pen = new Pen(Color.FromArgb(235, 240, 248), 2.5f);
                g.DrawRectangle(pen, 6, 5, 20, 22);
                g.DrawLine(pen, 11, 5, 11, 27);
                using var line = new Pen(Color.FromArgb(150, 170, 200), 1.6f);
                g.DrawLine(line, 15, 12, 22, 12);
                g.DrawLine(line, 15, 17, 22, 17);
                if (unread != 0)
                {
                    using var dot = new SolidBrush(unread < 0
                        ? Color.FromArgb(150, 150, 150)       // service down
                        : Color.FromArgb(240, 170, 20));      // unread waiting
                    g.FillEllipse(dot, 19, 19, 11, 11);
                }
            }
            var h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        static void Warn(string msg) =>
            MessageBox.Show(msg, "Currents", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        void Quit()
        {
            _poll.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            _generated?.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _http.Dispose(); _poll.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

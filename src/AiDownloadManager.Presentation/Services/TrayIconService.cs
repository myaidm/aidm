using System.Drawing;
using WinForms = System.Windows.Forms;

namespace AiDownloadManager.Presentation.Services;

/// <summary>
/// System-tray icon (NotifyIcon from System.Windows.Forms — part of the
/// WindowsDesktop SDK, no new package). Pure view concern: it only shows
/// the window or exits the application. Disposed on shutdown so no icon
/// lingers in the tray.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Ai Download Manager",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("Show Ai Download Manager");
        showItem.Click += (_, _) => showWindow();
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitApplication();
        menu.Items.Add(showItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}

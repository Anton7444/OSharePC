using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OShareSender.Ui;

namespace OShareSender;

/// <summary>Main window, styled after LocalSend: left nav rail, Send tab (selection card +
/// nearby device tiles), Receive tab, Log tab, whole-window drag-to-send and themed dialogs.</summary>
public sealed class MainForm : Form
{
    private const int ColumnWidth = 680;

    private readonly SenderEngine _engine;
    private NotifyIcon _tray = null!;
    private readonly List<FileEntry> _entries = [];
    private readonly HashSet<string> _pathSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeviceTile> _tiles = [];
    private System.Windows.Forms.Timer? _pruneTimer;
    private TransferTask? _task;
    private bool _exiting;
    private bool _sending;
    private ulong? _selectedAddress;
    private ulong? _activeAddress;
    private string? _completedTaskId;
    private bool _trayDisposed;

    // shell
    private readonly NavRail _rail;
    private readonly ThemedPanel _content;
    private ThemedPanel _sendTab = null!;
    private ThemedPanel _receiveTab = null!;
    private ThemedPanel _logTab = null!;
    private readonly DropOverlay _overlay;

    // send tab
    private ThemedPanel _scroll = null!;
    private ThemedPanel _column = null!;
    private Card _selCard = null!;
    private DropZone _dropZone = null!;
    private TransparentPanel _selFilled = null!;
    private TextBlock _selSummary = null!;
    private ThemedFlow _thumbStrip = null!;
    private AppButton _clearBtn = null!;
    private AppButton _addBtn = null!;
    private ScanButton _scan = null!;
    private ThemedPanel _devList = null!;
    private EmptyDeviceHint _devHint = null!;
    private FooterBar _footer = null!;
    private TextBlock _statusText = null!;
    private RoundedProgressBar _footerBar = null!;
    private AppDropdown _modeBox = null!;
    private AppButton _sendBtn = null!;
    private TextBlock _selHeader = null!;
    private TextBlock _devHeader = null!;

    // receive tab
    private ThemedPanel _rxScroll = null!;
    private ThemedPanel _rxColumn = null!;
    private Card _rxStatusCard = null!;
    private TextBlock _rxStatusTitle = null!;
    private TextBlock _rxStatusSubtitle = null!;
    private Card _rxStorageCard = null!;
    private TextBlock _rxStoragePath = null!;
    private AppButton _rxOpenFolderBtn = null!;
    private AppButton _rxChangeFolderBtn = null!;
    private Card _rxFilesCard = null!;
    private ThemedPanel _rxFileList = null!;
    private TextBlock _rxEmptyHint = null!;
    private TextBlock _rxHeaderStatus = null!;
    private TextBlock _rxHeaderStorage = null!;
    private TextBlock _rxHeaderFiles = null!;
    private readonly List<ReceivedFileTile> _rxTiles = new();

    // log tab
    private TextBlock _logTitle = null!;
    private AppButton _openLogBtn = null!;
    private TextBox _log = null!;

    [DllImport("dwmapi.dll")]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm()
    {
        Theme.Load();

        // The ARM build is commonly used on high-DPI Windows laptops. Explicit
        // DPI scaling keeps the hand-laid-out shell from being clipped at 150%+.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Text = "OsharePC";
        Lang.Load();
        Size = new Size(960, 660);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.App;

        _engine = new SenderEngine();
        _engine.DeviceSeen += OnDeviceSeen;
        _engine.Scanner.DeviceExpired += _ => QueueUi(RefreshDevices);
        _engine.TransferStateChanged += OnTransferState;
        _engine.ConfirmIncomingOShare = ConfirmIncomingAsync;
        _engine.ConfirmIncomingTransfer = ConfirmIncomingTransferAsync;
        _engine.ReceiveProgress += (done, total) => QueueUi(() => OnReceiveProgress(done, total));
        _engine.ReceiveCompleted += (sender, files) => QueueUi(() => OnReceiveCompleted(sender, files));
        _engine.ReceiveFailed += (sender, err) => QueueUi(() => OnReceiveFailed(sender, err));

        _rail = new NavRail();
        _content = new ThemedPanel();
        _sendTab = BuildSendTab();
        _receiveTab = BuildReceiveTab();
        _logTab = BuildLogTab();
        _overlay = new DropOverlay();

        SuspendLayout();
        _content.Controls.Add(_sendTab);
        _content.Controls.Add(_receiveTab);
        _content.Controls.Add(_logTab);
        _content.Controls.Add(_overlay);
        _sendTab.Dock = DockStyle.Fill;
        _receiveTab.Dock = DockStyle.Fill;
        _logTab.Dock = DockStyle.Fill;
        _overlay.Dock = DockStyle.Fill;
        _receiveTab.Visible = false;
        _logTab.Visible = false;

        _rail.SelectedIndexChanged += i =>
        {
            _sendTab.Visible = i == 0;
            _receiveTab.Visible = i == 1;
            _logTab.Visible = i == 2;
        };

        Controls.Add(_content);
        _content.Dock = DockStyle.Fill;
        _content.Controls.SetChildIndex(_sendTab, 3);
        _content.Controls.SetChildIndex(_receiveTab, 2);
        _content.Controls.SetChildIndex(_logTab, 1);
        _content.Controls.SetChildIndex(_overlay, 0);
        Controls.Add(_rail);
        _rail.Dock = DockStyle.Left;
        ResumeLayout();

        BuildTray();
        SetupDragDrop();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && _tray.Visible && !_exiting)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "OsharePC", Lang.T("Dialog.StillRunning"), ToolTipIcon.Info);
            }
        };
        Shown += async (_, _) => await StartEngineAsync();
        Lang.Changed += OnLangChanged;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutFooter();
        LayoutSendTab();
        LayoutReceiveTab();
    }

    private void OnLangChanged()
    {
        // headers
        _selHeader.Text = Lang.T("Header.Selection");
        _devHeader.Text = Lang.T("Header.NearbyDevices");
        _rxHeaderStatus.Text = Lang.T("Header.ReceiveStatus");
        _rxHeaderStorage.Text = Lang.T("Header.Storage");
        _rxHeaderFiles.Text = Lang.T("Header.ReceivedFiles");
        _logTitle.Text = Lang.T("Header.Log");

        // buttons & labels
        _sendBtn.Text = Lang.T("Btn.Send");
        _clearBtn.Text = Lang.T("Btn.Clear");
        _addBtn.Text = Lang.T("Btn.Add");
        _rxStatusTitle.Text = Lang.T("Status.ReadyReceive");
        _rxOpenFolderBtn.Text = Lang.T("Btn.OpenFolder");
        _rxChangeFolderBtn.Text = Lang.T("Btn.ChangeFolder");
        _rxEmptyHint.Text = Lang.T("Hint.NoReceived");
        _openLogBtn.Text = Lang.T("Btn.OpenLogFile");

        // mode dropdown
        var savedIdx = _modeBox.SelectedIndex;
        _modeBox.Items = [Lang.T("Mode.Auto"), Lang.T("Mode.Alliance"), Lang.T("Mode.OShare")];
        _modeBox.SelectedIndex = savedIdx;

        // file summary
        RebuildSelection();

        // tray menu
        if (_tray.ContextMenuStrip is { } menu)
        {
            menu.Items.Clear();
            menu.Items.Add(Lang.T("Tray.Show"), null, (_, _) => ShowFromTray());
            menu.Items.Add(Lang.T("Tray.Exit"), null, (_, _) => { _exiting = true; Close(); });
        }

        // invalidate all controls for repaint
        Invalidate(true);
    }

    private bool? _appliedDark;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Changed += ApplyTheme;
        // DWM reads the caption attribute when the handle is created — set it there.
        // On Win10 a runtime change is ignored, so theme flips recreate the handle.
        SetCaptionAttribute();
        _appliedDark = Theme.IsDark;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Theme.Changed -= ApplyTheme;
        base.OnHandleDestroyed(e);
    }

    private void SetCaptionAttribute()
    {
        var value = Theme.IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref value, 4); } catch { }
    }

    private void ApplyTheme()
    {
        if (_appliedDark == Theme.IsDark) return;
        _appliedDark = Theme.IsDark;
        if (IsHandleCreated)
        {
            try { BeginInvoke(RecreateHandle); } catch (InvalidOperationException) { }
        }
        if (_log is not null && !_log.IsDisposed)
        {
            _log.BackColor = Theme.IsDark ? Color.FromArgb(24, 24, 28) : Theme.Scheme.SurfaceContainerHigh;
            _log.ForeColor = Theme.IsDark ? Color.FromArgb(220, 220, 225) : Theme.Scheme.OnSurface;
        }
    }

    // ---------------------------------------------------------------- send tab

    private ThemedPanel BuildSendTab()
    {
        var tab = new ThemedPanel();

        _footer = new FooterBar { Dock = DockStyle.Bottom, Height = 64 };
        _statusText = new TextBlock(Lang.T("Status.Starting"), 9f, dim: true) { Bounds = new Rectangle(16, 0, 200, 64) };
        _footerBar = new RoundedProgressBar { Visible = false };
        _modeBox = new AppDropdown(220, 40);
        _modeBox.Items = [Lang.T("Mode.Auto"), Lang.T("Mode.Alliance"), Lang.T("Mode.OShare")];
        _modeBox.SelectedIndex = 0;
        _sendBtn = new AppButton(Lang.T("Btn.Send"), Glyphs.Send, AppButton.AppBtnStyle.Filled, 148, 40)
        {
            Anchor = AnchorStyles.Right,
        };
        _sendBtn.Click += async (_, _) => await SendAsync();
        _footer.Controls.AddRange([_statusText, _footerBar, _modeBox, _sendBtn]);
        _footer.Resize += (_, _) => LayoutFooter();

        _scroll = new ThemedPanel { Dock = DockStyle.Fill, AutoScroll = true };
        _scroll.Resize += (_, _) => LayoutSendTab();

        _column = new ThemedPanel { Size = new Size(ColumnWidth, 400) };

        var selHeader = _selHeader = new TextBlock(Lang.T("Header.Selection"), 12f, semibold: true);
        _devList = new ThemedPanel();

        _selCard = new Card();
        _dropZone = new DropZone();
        _dropZone.BrowseRequested += AddFilesDialog;

        _selFilled = new TransparentPanel { Visible = false };
        _clearBtn = new AppButton(Lang.T("Btn.Clear"), Glyphs.Delete, AppButton.AppBtnStyle.Text, 96, 34);
        _clearBtn.Click += (_, _) => ClearSelection();
        var summary = _selSummary = new TextBlock("", 9.5f, dim: true);
        _thumbStrip = new ThemedFlow { AutoScroll = true };
        _addBtn = new AppButton(Lang.T("Btn.Add"), Glyphs.Add, AppButton.AppBtnStyle.Tonal, 110, 38);
        _addBtn.Click += (_, _) => AddFilesDialog();

        _selFilled.Controls.Add(_clearBtn);
        _selFilled.Controls.Add(summary);
        _selFilled.Controls.Add(_thumbStrip);
        _selFilled.Controls.Add(_addBtn);
        _selFilled.Resize += (_, _) => LayoutSelectionCard();
        _selCard.Controls.Add(_dropZone);
        _selCard.Controls.Add(_selFilled);
        _selCard.Resize += (_, _) => LayoutSelectionCard();

        var devHeader = _devHeader = new TextBlock(Lang.T("Header.NearbyDevices"), 12f, semibold: true);
        _scan = new ScanButton();
        _scan.Click += (_, _) =>
        {
            _scan.Spinning = true;
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += (_, _) => { _scan.Spinning = false; t.Stop(); t.Dispose(); };
            t.Start();
        };
        _devHint = new EmptyDeviceHint();
        _devList.Controls.Add(_devHint);

        _column.Controls.Add(selHeader);
        _column.Controls.Add(_selCard);
        _column.Controls.Add(devHeader);
        _column.Controls.Add(_scan);
        _column.Controls.Add(_devList);
        _scroll.Controls.Add(_column);
        tab.Controls.Add(_footer);
        tab.Controls.Add(_scroll);
        LayoutSelectionCard();
        return tab;
    }

    private bool _wantProgress;
    private void LayoutFooter()
    {
        var w = _footer.ClientSize.Width;
        var h = _footer.ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        const int pad = 16;
        const int gap = 12;
        const int modeW = 220;
        const int barW = 220;
        var sendLeft = w - pad - _sendBtn.Width;
        _sendBtn.Location = new Point(Math.Max(pad, sendLeft), (h - _sendBtn.Height) / 2);
        var modeLeft = _sendBtn.Left - gap - modeW;
        _modeBox.Location = new Point(modeLeft, (h - _modeBox.Height) / 2);
        var barLeft = modeLeft - gap - barW;
        var barFits = barLeft >= pad;
        _footerBar.Visible = _wantProgress && (barFits || _footerBar.Value is > 0 and < 1);
        if (_footerBar.Visible)
        {
            var bw = barFits ? barW : Math.Min(barW, Math.Max(40, modeLeft - pad - gap));
            var bl = barFits ? barLeft : modeLeft - gap - bw;
            _footerBar.Bounds = new Rectangle(bl, (h - _footerBar.Height) / 2, bw, 10);
        }
        else
        {
            _footerBar.Bounds = new Rectangle(barLeft, (h - _footerBar.Height) / 2, barW, 10);
        }
        var statusRight = _footerBar.Visible ? _footerBar.Left : _modeBox.Left;
        var statusW = Math.Max(0, statusRight - pad - gap);
        _statusText.Bounds = new Rectangle(pad, 0, statusW, h);
    }

    private void LayoutSelectionCard()
    {
        var w = _selCard.ClientSize.Width;
        var h = _selCard.ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        if (_dropZone.Visible)
            _dropZone.Bounds = new Rectangle(12, 12, w - 24, Math.Max(0, h - 24));
        _selFilled.Bounds = new Rectangle(0, 0, w, h);
        _clearBtn.Location = new Point(w - _clearBtn.Width - 12, 10);
        _selSummary.Bounds = new Rectangle(16, 16, Math.Max(0, w - _clearBtn.Width - 48), 22);
        _thumbStrip.Bounds = new Rectangle(16, 44, Math.Max(0, w - 32), Math.Max(0, h - 44 - 52));
        _addBtn.Location = new Point(w - _addBtn.Width - 16, h - _addBtn.Height - 14);
    }

    private void LayoutSendTab()
    {
        var w = Math.Min(ColumnWidth, Math.Max(360, _scroll.ClientSize.Width - 16));
        _column.Width = w;
        _column.Left = Math.Max(8, (_scroll.ClientSize.Width - w) / 2);

        var y = 22;
        _column.Controls[0].Bounds = new Rectangle(0, y, w, 26);      // "Selection"
        y += 30;
        _selCard.Width = w;
        _selCard.Height = _entries.Count > 0 ? 214 : 206;
        _selCard.Location = new Point(0, y);
        y += _selCard.Height + 26;

        _column.Controls[2].Bounds = new Rectangle(0, y + 8, w - 60, 26);  // "Nearby devices"
        _scan.Location = new Point(w - 42, y);
        y += 52;
        _devList.Bounds = new Rectangle(0, y, w, DeviceListHeight());
        y += _devList.Height + 24;
        _column.Height = y;
    }

    private int DeviceListHeight() =>
        _tiles.Count > 0 ? _tiles.Count * (DeviceTile.TileHeight + 10) - 10 : 96;

    // ---------------------------------------------------------------- receive tab

    private ThemedPanel BuildReceiveTab()
    {
        var tab = new ThemedPanel();

        _rxScroll = new ThemedPanel { Dock = DockStyle.Fill, AutoScroll = true };
        _rxScroll.Resize += (_, _) => LayoutReceiveTab();

        _rxColumn = new ThemedPanel { Size = new Size(ColumnWidth, 500) };

        // 1. Status Section
        _rxHeaderStatus = new TextBlock(Lang.T("Header.ReceiveStatus"), 12f, semibold: true);
        _rxStatusCard = new Card();
        _rxStatusTitle = new TextBlock(Lang.T("Status.ReadyReceive"), 11f, semibold: true);
        _rxStatusSubtitle = new TextBlock("Starting receive engine…", 9f, dim: true);

        _rxStatusCard.Controls.Add(_rxStatusTitle);
        _rxStatusCard.Controls.Add(_rxStatusSubtitle);
        _rxStatusCard.Resize += (_, _) =>
        {
            var cw = _rxStatusCard.ClientSize.Width;
            _rxStatusTitle.Bounds = new Rectangle(18, 16, Math.Max(0, cw - 36), 24);
            _rxStatusSubtitle.Bounds = new Rectangle(18, 44, Math.Max(0, cw - 36), 22);
        };

        // 2. Storage Section
        _rxHeaderStorage = new TextBlock(Lang.T("Header.Storage"), 12f, semibold: true);
        _rxStorageCard = new Card();
        _rxStoragePath = new TextBlock(_engine.Receiver.SaveDirectory, 9.5f);
        _rxOpenFolderBtn = new AppButton(Lang.T("Btn.OpenFolder"), Glyphs.OpenFolder, AppButton.AppBtnStyle.Tonal, 130, 36);
        _rxOpenFolderBtn.Click += (_, _) => OpenSaveDirectory();
        _rxChangeFolderBtn = new AppButton(Lang.T("Btn.ChangeFolder"), Glyphs.OpenFile, AppButton.AppBtnStyle.Tonal, 140, 36);
        _rxChangeFolderBtn.Click += (_, _) => ChangeSaveDirectory();

        _rxStorageCard.Controls.Add(_rxStoragePath);
        _rxStorageCard.Controls.Add(_rxOpenFolderBtn);
        _rxStorageCard.Controls.Add(_rxChangeFolderBtn);
        _rxStorageCard.Resize += (_, _) =>
        {
            var cw = _rxStorageCard.ClientSize.Width;
            var ch = _rxStorageCard.ClientSize.Height;
            var btnY = (ch - 36) / 2;
            _rxChangeFolderBtn.Location = new Point(cw - _rxChangeFolderBtn.Width - 14, btnY);
            _rxOpenFolderBtn.Location = new Point(_rxChangeFolderBtn.Left - _rxOpenFolderBtn.Width - 8, btnY);
            var textW = Math.Max(0, _rxOpenFolderBtn.Left - 28);
            _rxStoragePath.Bounds = new Rectangle(18, (ch - 22) / 2, textW, 22);
        };

        // 3. Received Files Section
        _rxHeaderFiles = new TextBlock(Lang.T("Header.ReceivedFiles"), 12f, semibold: true);
        _rxFilesCard = new Card();
        _rxEmptyHint = new TextBlock(Lang.T("Hint.NoReceived"), 9.5f, dim: true);
        _rxFileList = new ThemedPanel();

        _rxFilesCard.Controls.Add(_rxEmptyHint);
        _rxFilesCard.Controls.Add(_rxFileList);
        _rxFilesCard.Resize += (_, _) =>
        {
            var cw = _rxFilesCard.ClientSize.Width;
            var ch = _rxFilesCard.ClientSize.Height;
            if (_rxTiles.Count == 0)
            {
                _rxEmptyHint.Visible = true;
                _rxFileList.Visible = false;
                _rxEmptyHint.Bounds = new Rectangle(18, (ch - 24) / 2, Math.Max(0, cw - 36), 24);
            }
            else
            {
                _rxEmptyHint.Visible = false;
                _rxFileList.Visible = true;
                _rxFileList.Bounds = new Rectangle(12, 12, Math.Max(0, cw - 24), Math.Max(0, ch - 24));
            }
        };

        _rxColumn.Controls.Add(_rxHeaderStatus);
        _rxColumn.Controls.Add(_rxStatusCard);
        _rxColumn.Controls.Add(_rxHeaderStorage);
        _rxColumn.Controls.Add(_rxStorageCard);
        _rxColumn.Controls.Add(_rxHeaderFiles);
        _rxColumn.Controls.Add(_rxFilesCard);

        _rxScroll.Controls.Add(_rxColumn);
        tab.Controls.Add(_rxScroll);
        return tab;
    }

    private void LayoutReceiveTab()
    {
        var w = Math.Min(ColumnWidth, Math.Max(360, _rxScroll.ClientSize.Width - 16));
        _rxColumn.Width = w;
        _rxColumn.Left = Math.Max(8, (_rxScroll.ClientSize.Width - w) / 2);

        var y = 22;
        _rxHeaderStatus.Bounds = new Rectangle(0, y, w, 26);
        y += 30;

        _rxStatusCard.Width = w;
        _rxStatusCard.Height = 84;
        _rxStatusCard.Location = new Point(0, y);
        y += _rxStatusCard.Height + 24;

        _rxHeaderStorage.Bounds = new Rectangle(0, y, w, 26);
        y += 30;

        _rxStorageCard.Width = w;
        _rxStorageCard.Height = 64;
        _rxStorageCard.Location = new Point(0, y);
        y += _rxStorageCard.Height + 24;

        _rxHeaderFiles.Bounds = new Rectangle(0, y, w, 26);
        y += 30;

        var filesHeight = _rxTiles.Count > 0 ? (_rxTiles.Count * (ReceivedFileTile.TileHeight + 8) + 24) : 96;
        _rxFilesCard.Width = w;
        _rxFilesCard.Height = filesHeight;
        _rxFilesCard.Location = new Point(0, y);
        y += _rxFilesCard.Height + 24;

        _rxColumn.Height = y;
    }

    private void OpenSaveDirectory()
    {
        var dir = _engine.Receiver.SaveDirectory;
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    private void ChangeSaveDirectory()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = _engine.Receiver.SaveDirectory,
            Description = Lang.T("Header.Storage"),
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            _engine.Receiver.SaveDirectory = dlg.SelectedPath;
            _rxStoragePath.Text = dlg.SelectedPath;
        }
    }

    // ---------------------------------------------------------------- log tab

    private ThemedPanel BuildLogTab()
    {
        var tab = new ThemedPanel { Padding = new Padding(24, 20, 24, 20) };
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(1) };
        var header = new ThemedPanel { Dock = DockStyle.Top, Height = 56, CardBackground = true };
        var title = _logTitle = new TextBlock(Lang.T("Header.Log"), 12f, semibold: true);
        var openBtn = _openLogBtn = new AppButton(Lang.T("Btn.OpenLogFile"), Glyphs.OpenFile, AppButton.AppBtnStyle.Tonal, 150, 36);
        openBtn.Click += (_, _) =>
        {
            if (Log.LogFilePath.Length > 0)
                try { Process.Start("explorer.exe", $"/select,\"{Log.LogFilePath}\""); } catch { }
        };
        header.Resize += (_, _) =>
        {
            title.Bounds = new Rectangle(16, 0, 200, 56);
            openBtn.Location = new Point(header.ClientSize.Width - openBtn.Width - 12, 10);
        };
        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.IsDark ? Color.FromArgb(24, 24, 28) : Theme.Scheme.SurfaceContainerHigh,
            ForeColor = Theme.IsDark ? Color.FromArgb(220, 220, 225) : Theme.Scheme.OnSurface,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
        };
        card.Controls.Add(_log);
        card.Controls.Add(header);
        header.Controls.Add(title);
        header.Controls.Add(openBtn);
        tab.Controls.Add(card);
        return tab;
    }

    // ---------------------------------------------------------------- tray

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Text = "OsharePC",
            Icon = AppIcon.App,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(Lang.T("Tray.Show"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Lang.T("Tray.Exit"), null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ---------------------------------------------------------------- drag & drop

    private void SetupDragDrop()
    {
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            e.Effect = DragDropEffects.None;
            HideDropOverlay();
            return;
        }
        e.Effect = DragDropEffects.Copy;
        ShowDropOverlay();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            e.Effect = DragDropEffects.None;
            HideDropOverlay();
            return;
        }
        e.Effect = DragDropEffects.Copy;
        if (!_overlay.Visible) ShowDropOverlay();
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        QueueUi(() =>
        {
            var pos = PointToClient(Cursor.Position);
            if (!ClientRectangle.Contains(pos)) HideDropOverlay();
        });
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var files = ExpandPaths(paths);
        if (files.Count > 0)
        {
            AddPaths(files);
            _rail.SelectedIndex = 0;
            _sendTab.Visible = true;
            _receiveTab.Visible = false;
            _logTab.Visible = false;
        }
    }

    private void ShowDropOverlay()
    {
        _overlay.BringToFront();
        _overlay.Visible = true;
    }

    private void HideDropOverlay() => _overlay.Visible = false;

    /// <summary>Expands dropped directories into files (recursively, capped) — mirrors
    /// LocalSend accepting folders from the drop target.</summary>
    private static List<string> ExpandPaths(string[] paths)
    {
        const int maxFiles = 500;
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        files.Add(f);
                        if (files.Count >= maxFiles) return files;
                    }
                }
                catch { }
            }
            else if (File.Exists(p))
            {
                files.Add(p);
                if (files.Count >= maxFiles) return files;
            }
        }
        return files;
    }

    // ---------------------------------------------------------------- files

    private void AddFilesDialog()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = Lang.T("File.PickTitle") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddPaths(dlg.FileNames.ToList());
    }

    private void AddPaths(List<string> paths)
    {
        foreach (var p in paths)
        {
            if (!_pathSet.Add(p)) continue;
            long size = 0;
            try { size = new FileInfo(p).Length; } catch { }
            _entries.Add(new FileEntry(p, Path.GetFileName(p), size));
        }
        RebuildSelection();
        Restage();
    }

    private void RemoveEntry(FileEntry entry)
    {
        _pathSet.Remove(entry.FullPath);
        _entries.Remove(entry);
        RebuildSelection();
        Restage();
    }

    private void ClearSelection()
    {
        _pathSet.Clear();
        _entries.Clear();
        RebuildSelection();
        Restage();
    }

    private void RebuildSelection()
    {
        _thumbStrip.SuspendLayout();
        foreach (Control c in _thumbStrip.Controls.Cast<Control>().ToArray())
        {
            c.Dispose();
        }
        _thumbStrip.Controls.Clear();
        foreach (var entry in _entries)
        {
            var thumb = new FileThumb(entry);
            thumb.RemoveRequested += t => RemoveEntry(t.Entry);
            _thumbStrip.Controls.Add(thumb);
        }
        _thumbStrip.ResumeLayout();
        _dropZone.Visible = _entries.Count == 0;
        _selFilled.Visible = _entries.Count > 0;
        _selSummary.Text = _entries.Count > 0
            ? string.Format(Lang.T("File.Summary"), _entries.Count, FormatSize(_entries.Sum(e => e.Size)))
            : "";
        LayoutSendTab();
    }

    private void Restage() =>
        _task = _entries.Count > 0 ? _engine.StageFiles(_entries.Select(e => e.FullPath)) : null;

    // ---------------------------------------------------------------- engine

    private async Task StartEngineAsync()
    {
        foreach (var line in Log.Snapshot()) AppendLog(line);
        Log.Logged += AppendLog;
        try
        {
            await _engine.StartAsync(SenderEngine.DefaultPort);
            _pruneTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pruneTimer.Tick += (_, _) => RefreshDevices();
            _pruneTimer.Start();
            _rxStatusSubtitle.Text = $"Advertising as '{_engine.Advertiser.DeviceName}' (IP: {_engine.Lan?.IpString ?? "Ready"})";
        }
        catch (Exception ex)
        {
            Log.Error("engine start failed", ex);
            AppDialog.Error(this, Lang.T("Dialog.EngineStartFailed"), ex.Message);
        }
    }

    private Task<bool> ConfirmIncomingAsync(OShareP2pOffer offer)
    {
        if (IsDisposed || Disposing) return Task.FromResult(false);
        if (!IsHandleCreated) return Task.FromResult(false);
        if (InvokeRequired)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueUi(() =>
            {
                try { result.SetResult(ConfirmIncomingAsync(offer).GetAwaiter().GetResult()); }
                catch { result.TrySetResult(false); }
            });
            return result.Task;
        }
        if (SettingsStore.Current.QuickSaveMode == 2 &&
            (WindowState == FormWindowState.Minimized || !Visible))
            return Task.FromResult(true);

        var answer = MessageBox.Show(this,
            $"A OShare device ({offer.SenderId}) wants to send files to this PC.\n\nAccept the incoming transfer?",
            "Incoming OsharePC transfer", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    private Task<bool> ConfirmIncomingTransferAsync(string senderName, string mimeType, string fileCount)
    {
        if (IsDisposed || Disposing) return Task.FromResult(false);
        if (!IsHandleCreated) return Task.FromResult(false);
        if (InvokeRequired)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueUi(() =>
            {
                try { result.SetResult(ConfirmIncomingTransferAsync(senderName, mimeType, fileCount).GetAwaiter().GetResult()); }
                catch { result.TrySetResult(false); }
            });
            return result.Task;
        }
        if (SettingsStore.Current.QuickSaveMode == 2 &&
            (WindowState == FormWindowState.Minimized || !Visible))
            return Task.FromResult(true);

        var msg = string.Format(Lang.T("Dialog.IncomingPrompt"), senderName, fileCount, mimeType);
        var answer = MessageBox.Show(this,
            msg,
            Lang.T("Dialog.IncomingTransfer"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            // default to declining — an accidental Enter must not accept a transfer
            MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    private void OnReceiveProgress(long done, long total)
    {
        if (total > 0)
        {
            _wantProgress = true;
            _footerBar.Visible = true;
            _footerBar.SetValue(Math.Clamp((double)done / total, 0.0, 1.0));
            _statusText.Text = $"{Lang.T("Status.Receiving")} {FormatSize(done)} / {FormatSize(total)}";
            _rxStatusSubtitle.Text = $"Receiving: {FormatSize(done)} / {FormatSize(total)} ({_footerBar.Value * 100:F0}%)";
        }
    }

    private void OnReceiveCompleted(string senderName, IReadOnlyList<string> files)
    {
        _wantProgress = false;
        _footerBar.Visible = false;
        _statusText.Text = $"Received {files.Count} file(s) from {senderName}";
        _rxStatusSubtitle.Text = $"Last received {files.Count} file(s) from {senderName} at {DateTime.Now:HH:mm:ss}";

        _rxFileList.SuspendLayout();
        foreach (var file in files)
        {
            var tile = new ReceivedFileTile(file, senderName) { Width = Math.Max(200, _rxFileList.ClientSize.Width) };
            tile.OpenFileRequested += f =>
            {
                try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { }
            };
            tile.ShowInFolderRequested += f =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f}\"") { UseShellExecute = true }); } catch { }
            };
            _rxTiles.Insert(0, tile);
        }

        _rxFileList.Controls.Clear();
        var y = 0;
        foreach (var tile in _rxTiles)
        {
            tile.Bounds = new Rectangle(0, y, Math.Max(200, _rxFileList.ClientSize.Width), ReceivedFileTile.TileHeight);
            _rxFileList.Controls.Add(tile);
            y += ReceivedFileTile.TileHeight + 8;
        }
        _rxFileList.Height = y;
        _rxFileList.ResumeLayout();

        LayoutReceiveTab();

        try
        {
            _tray.ShowBalloonTip(3000, "OsharePC",
                $"Received {files.Count} file(s) from {senderName}", ToolTipIcon.Info);
        }
        catch { }
    }

    private void OnReceiveFailed(string senderName, string error)
    {
        _wantProgress = false;
        _footerBar.Visible = false;
        _statusText.Text = $"Receive failed: {error}";
        _rxStatusSubtitle.Text = $"Failed from {senderName}: {error}";
    }

    private async Task SendAsync()
    {
        if (_selectedAddress is null)
        {
            AppDialog.Info(this, Lang.T("Btn.Send"), Lang.T("Dialog.SelectPhoneFirst"));
            return;
        }
        Restage();
        if (_task is null)
        {
            AppDialog.Info(this, Lang.T("Btn.Send"), Lang.T("Dialog.AddFilesFirst"));
            return;
        }
        var tile = _tiles.FirstOrDefault(t => t.Device.Address == _selectedAddress);
        if (tile is null)
        {
            _selectedAddress = null;
            AppDialog.Info(this, Lang.T("Btn.Send"), Lang.T("Dialog.SelectPhoneFirst"));
            return;
        }

        _sendBtn.Enabled = false;
        _modeBox.Enabled = false;
        _sending = true;
        _activeAddress = tile.Device.Address;
        try
        {
            var flow = _modeBox.SelectedIndex switch
            {
                1 => SendFlow.OConnectLan,
                2 => SendFlow.OShareHotspot,
                _ => SendFlow.Auto,
            };
            await _engine.SendToAsync(tile.Device, flow);
        }
        catch (Exception ex)
        {
            Log.Error("send failed", ex);
            AppDialog.Error(this, Lang.T("Dialog.SendFailed"), ex.Message);
        }
        finally
        {
            _sending = false;
            _activeAddress = null;
            _sendBtn.Enabled = true;
            _modeBox.Enabled = true;
        }
    }

    private void OnDeviceSeen(PhoneDevice device)
    {
        if (InvokeRequired) { QueueUi(RefreshDevices); return; }
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var devices = _engine.Scanner.Devices.OrderByDescending(d => d.LastSeen).ToList();
        var current = devices.Select(d => d.Address).ToHashSet();

        foreach (var tile in _tiles.Where(t => !current.Contains(t.Device.Address)).ToList())
        {
            if (_selectedAddress == tile.Device.Address) _selectedAddress = null;
            _tiles.Remove(tile);
            _devList.Controls.Remove(tile);
            tile.Dispose();
        }

        foreach (var d in devices)
        {
            var tile = _tiles.FirstOrDefault(t => t.Device.Address == d.Address);
            if (tile is null)
            {
                tile = new DeviceTile(d);
                tile.TileClicked += OnTileClicked;
                _tiles.Add(tile);
                _devList.Controls.Add(tile);
            }
            else
            {
                tile.UpdateDevice(d);
            }
            tile.Selected = d.Address == _selectedAddress;
            if (_sending && _activeAddress == d.Address)
                tile.SetProgress(_statusText.Text, _footerBar.Visible ? _footerBar.Value : null);
            else if (!_sending)
                tile.SetProgress(null, null);
        }

        var ordered = _tiles.OrderByDescending(t => t.Device.LastSeen).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Bounds = new Rectangle(0, i * (DeviceTile.TileHeight + 10), _devList.Width, DeviceTile.TileHeight);
            _devList.Controls.SetChildIndex(ordered[i], ordered.Count - 1 - i);
        }
        _devHint.Bounds = new Rectangle(0, 0, _devList.Width, 96);
        _devHint.Visible = _tiles.Count == 0;
        LayoutSendTab();
    }

    private void OnTileClicked(DeviceTile tile)
    {
        _selectedAddress = tile.Device.Address;
        foreach (var t in _tiles) t.Selected = t.Device.Address == _selectedAddress;
    }

    private void OnTransferState(string taskId, string state)
    {
        // engine events arrive from BLE/Kestrel threads — marshal to the UI thread
        if (InvokeRequired) { QueueUi(() => OnTransferState(taskId, state)); return; }

        string display = state;
        double? progress = null;
        var m = Regex.Match(state, @"^(\d+)/(\d+)$");
        if (m.Success)
        {
            var sent = long.Parse(m.Groups[1].Value);
            var total = long.Parse(m.Groups[2].Value);
            progress = total > 0 ? (double)sent / total : 0;
            display = $"{Lang.T("Status.SendingFiles")}  {(int)(progress * 100)}%  \u00b7  {FormatSize(sent)} / {FormatSize(total)}";
        }
        else if (state == "download complete")
        {
            progress = 1;
            display = Lang.T("Status.DownloadComplete");
        }

        _statusText.Text = display;
        _wantProgress = progress is not null;
        if (progress is not null) _footerBar.SetValue(progress.Value);
        LayoutFooter();
        SetTrayText($"OsharePC — {TrayState(state)}");

        if (_sending && _activeAddress is { } active)
        {
            var tile = _tiles.FirstOrDefault(t => t.Device.Address == active);
            tile?.SetProgress(display, progress);
        }

        if (progress >= 1 && state == "download complete" && _completedTaskId != taskId)
        {
            _completedTaskId = taskId;
            AppDialog.Show(this, Lang.T("Dialog.TransferComplete"), Lang.T("Dialog.PhoneDownloaded"), DialogKind.Success);
        }
    }

    private static string TrayState(string state)
    {
        if (state.StartsWith("Sending files", StringComparison.OrdinalIgnoreCase)) return Lang.T("Status.Sending");
        if (state.Contains("downloading", StringComparison.OrdinalIgnoreCase)) return Lang.T("Status.Sending");
        if (state.Contains("complete", StringComparison.OrdinalIgnoreCase)) return Lang.T("Status.Done");
        return state.Length > 40 ? state[..40] : state;
    }

    // ---------------------------------------------------------------- log

    private void AppendLog(string line)
    {
        if (_log.IsDisposed) return;
        if (InvokeRequired) { QueueUi(() => AppendLog(line)); return; }
        if (_log.TextLength > 400_000) _log.Text = _log.Text[200_000..];
        _log.AppendText(line + Environment.NewLine);
    }

    private void QueueUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void SetTrayText(string text)
    {
        if (_trayDisposed) return;
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    // ---------------------------------------------------------------- misc

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _log.AppendText(Log.LogFilePath.Length > 0 ? $"log file: {Log.LogFilePath}{Environment.NewLine}" : "");
        LayoutFooter();
        LayoutSendTab();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _exiting = true;
        _trayDisposed = true;
        _tray.Visible = false;
        _tray.Dispose();
        _pruneTimer?.Stop();
        Log.Logged -= AppendLog;
        // dispose on a background thread — a blocking Kestrel/BLE teardown on the UI
        // thread used to leave the process alive after the tray icon disappeared
        Task.Run(async () =>
        {
            try { await _engine.DisposeAsync(); }
            catch { }
            finally { Application.Exit(); Environment.Exit(0); }
        });
        base.OnFormClosed(e);
    }

    private static string FormatSize(long n) => n switch
    {
        >= 1L << 30 => $"{n / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{n / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{n / (double)(1L << 10):F1} KB",
        _ => $"{n} B"
    };
}

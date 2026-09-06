namespace CatShareSender.Ui;

public enum LangId { En, ZhHant, ZhHans }

/// <summary>Lightweight i18n: static string table for EN / 繁體 / 简体, no .resx needed.</summary>
public static class Lang
{
    private static LangId _current = LangId.En;
    public static LangId Current
    {
        get => _current;
        set { if (_current != value) { _current = value; Changed?.Invoke(); } }
    }

    public static event Action? Changed;

    public static void Set(LangId id) => Current = id;

    /// <summary>Cycle to the next language.</summary>
    public static void Cycle()
    {
        Current = _current switch
        {
            LangId.En => LangId.ZhHant,
            LangId.ZhHant => LangId.ZhHans,
            _ => LangId.En,
        };
    }

    /// <summary>Short label shown in the NavRail picker.</summary>
    public static string PickerLabel => _current switch
    {
        LangId.En => "EN",
        LangId.ZhHant => "\u7e41",
        LangId.ZhHans => "\u7b80",
        _ => "EN",
    };

    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        return _current switch
        {
            LangId.ZhHant => row[1],
            LangId.ZhHans => row[2],
            _ => row[0],
        };
    }

    // ── persistence ──────────────────────────────────────────────────────

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatShare");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "lang.txt");

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var text = File.ReadAllText(ConfigFile).Trim();
            if (Enum.TryParse<LangId>(text, true, out var id)) _current = id;
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigFile, _current.ToString());
        }
        catch { }
    }

    // ── string table ─────────────────────────────────────────────────────
    // Each entry: key → [English, 繁體中文, 简体中文]

    private static readonly Dictionary<string, string[]> Table = new()
    {
        // Nav rail
        ["Nav.Send"]            = ["Send",          "\u50b3\u9001",          "\u53d1\u9001"],
        ["Nav.Receive"]         = ["Receive",       "\u63a5\u6536",          "\u63a5\u6536"],
        ["Nav.Log"]             = ["Log",           "\u65e5\u8a8c",          "\u65e5\u5fd7"],

        // Section headers
        ["Header.Selection"]    = ["Selection",     "\u5df2\u9078\u6a94\u6848", "\u5df2\u9009\u6587\u4ef6"],
        ["Header.NearbyDevices"]= ["Nearby devices", "\u9644\u8fd1\u7684\u88dd\u7f6e", "\u9644\u8fd1\u7684\u8bbe\u5907"],
        ["Header.ReceiveStatus"]= ["Receive Status", "\u63a5\u6536\u72c0\u614b", "\u63a5\u6536\u72b6\u6001"],
        ["Header.Storage"]      = ["Save Location", "\u5132\u5b58\u4f4d\u7f6e", "\u4fdd\u5b58\u4f4d\u7f6e"],
        ["Header.ReceivedFiles"]= ["Received Files", "\u5df2\u63a5\u6536\u6a94\u6848", "\u5df2\u63a5\u6536\u6587\u4ef6"],
        ["Header.Log"]          = ["Log",           "\u65e5\u8a8c",          "\u65e5\u5fd7"],

        // Buttons
        ["Btn.Send"]            = ["Send",          "\u50b3\u9001",          "\u53d1\u9001"],
        ["Btn.Clear"]           = ["Clear",         "\u6e05\u9664",          "\u6e05\u9664"],
        ["Btn.Add"]             = ["Add",           "\u65b0\u589e",          "\u6dfb\u52a0"],
        ["Btn.BrowseFiles"]     = ["Browse files",  "\u700f\u89bd\u6a94\u6848", "\u6d4f\u89c8\u6587\u4ef6"],
        ["Btn.OpenLogFile"]     = ["Open log file", "\u958b\u555f\u65e5\u8a8c\u6a94\u6848", "\u6253\u5f00\u65e5\u5fd7\u6587\u4ef6"],
        ["Btn.OpenFolder"]      = ["Open folder",   "\u958b\u555f\u8cc7\u6599\u593e", "\u6253\u5f00\u6587\u4ef6\u593e"],
        ["Btn.ChangeFolder"]    = ["Change folder…", "\u8b8a\u66f4\u8cc7\u6599\u593e…", "\u66f4\u6539\u6587\u4ef6\u593e…"],
        ["Btn.OpenFile"]        = ["Open file",     "\u958b\u555f\u6a94\u6848", "\u6253\u5f00\u6587\u4ef6"],
        ["Btn.Accept"]          = ["Accept",        "\u63a5\u53d7",          "\u63a5\u53d7"],
        ["Btn.Reject"]          = ["Reject",        "\u62d2\u7d55",          "\u62d2\u7edd"],
        ["Btn.OK"]              = ["OK",            "\u78ba\u5b9a",          "\u786e\u5b9a"],

        // Drop zone
        ["Drop.DragHere"]       = ["Drag & drop files here", "\u5c07\u6a94\u6848\u62d6\u653e\u5230\u9019\u88e1", "\u5c06\u6587\u4ef6\u62d6\u653e\u5230\u8fd9\u91cc"],
        ["Drop.OrBrowse"]       = ["or browse your computer", "\u6216\u5f9e\u96fb\u8166\u700f\u89bd", "\u6216\u4ece\u7535\u8111\u6d4f\u89c8"],
        ["Drop.PlaceItems"]     = ["Place items to share.", "\u653e\u7f6e\u8981\u5206\u4eab\u7684\u9805\u76ee\u3002", "\u653e\u7f6e\u8981\u5206\u4eab\u7684\u9879\u76ee\u3002"],

        // Empty device hint
        ["Hint.NoPhones"]       = ["No nearby phones yet", "\u5c1a\u7121\u9644\u8fd1\u7684\u624b\u6a5f", "\u6682\u65e0\u9644\u8fd1\u7684\u624b\u673a"],
        ["Hint.OpenApp"]        = ["Open {0} or CatShare on your phone to appear here",
                                   "\u5728\u624b\u6a5f\u4e0a\u958b\u555f {0} \u6216 CatShare \u4ee5\u986f\u793a\u5728\u6b64",
                                   "\u5728\u624b\u673a\u4e0a\u6253\u5f00 {0} \u6216 CatShare \u4ee5\u663e\u793a\u5728\u6b64"],
        ["Hint.NoReceived"]     = ["No files received yet. Send files from your phone via 互传 / OShare.",
                                   "\u5c1a\u672a\u63a5\u6536\u4efb\u4f55\u6a94\u6848\u3002\u8acb\u5f9e\u624b\u6a5f\u4e0a\u4f7f\u7528\u300c\u4e92\u50b3\u300d\u5206\u4eab\u6a94\u6848\u81f3\u6b64\u96fb\u8166\u3002",
                                   "\u5c1a\u672a\u63a5\u6536\u4efb\u4f55\u6587\u4ef6\u3002\u8bf7\u4ece\u624b\u673a\u4e0a\u4f7f\u7528\u201c\u4e92\u4f20\u201d\u5206\u4eab\u6587\u4ef6\u81f3\u6b64\u7535\u8111\u3002"],

        // Status messages
        ["Status.Starting"]     = ["starting\u2026", "\u6b63\u5728\u555f\u52d5\u2026", "\u6b63\u5728\u542f\u52a8\u2026"],
        ["Status.ReadyReceive"] = ["Ready to receive (OShare / 互传 discoverable)",
                                   "\u6e96\u5099\u63a5\u6536\uff08\u4e92\u50b3\u53ef\u641c\u5c0b\uff09",
                                   "\u51c6\u5907\u63a5\u6536\uff08\u4e92\u4f20\u53ef\u641c\u7d22\uff09"],
        ["Status.SendingFiles"] = ["Sending files\u2026", "\u6b63\u5728\u50b3\u9001\u6a94\u6848\u2026", "\u6b63\u5728\u53d1\u9001\u6587\u4ef6\u2026"],
        ["Status.DownloadComplete"] = ["Download complete \u2014 the phone received your files.",
                                       "\u4e0b\u8f09\u5b8c\u6210 \u2014 \u624b\u6a5f\u5df2\u6536\u5230\u60a8\u7684\u6a94\u6848\u3002",
                                       "\u4e0b\u8f7d\u5b8c\u6210 \u2014 \u624b\u673a\u5df2\u6536\u5230\u60a8\u7684\u6587\u4ef6\u3002"],
        ["Status.Sending"]      = ["sending\u2026", "\u50b3\u9001\u4e2d\u2026", "\u53d1\u9001\u4e2d\u2026"],
        ["Status.Done"]         = ["done",          "\u5b8c\u6210",          "\u5b8c\u6210"],

        // Dialogs
        ["Dialog.EngineStartFailed"] = ["Engine start failed", "\u5f15\u64ce\u555f\u52d5\u5931\u6557", "\u5f15\u64ce\u542f\u52a8\u5931\u8d25"],
        ["Dialog.SelectPhoneFirst"]  = ["Select a phone first.", "\u8acb\u5148\u9078\u64c7\u4e00\u90e8\u624b\u6a5f\u3002", "\u8bf7\u5148\u9009\u62e9\u4e00\u90e8\u624b\u673a\u3002"],
        ["Dialog.AddFilesFirst"]     = ["Add files to send first.", "\u8acb\u5148\u65b0\u589e\u8981\u50b3\u9001\u7684\u6a94\u6848\u3002", "\u8bf7\u5148\u6dfb\u52a0\u8981\u53d1\u9001\u7684\u6587\u4ef6\u3002"],
        ["Dialog.SendFailed"]        = ["Send failed", "\u50b3\u9001\u5931\u6557", "\u53d1\u9001\u5931\u8d25"],
        ["Dialog.TransferComplete"]  = ["Transfer complete", "\u50b3\u8f38\u5b8c\u6210", "\u4f20\u8f93\u5b8c\u6210"],
        ["Dialog.PhoneDownloaded"]   = ["The phone downloaded your files.",
                                        "\u624b\u6a5f\u5df2\u4e0b\u8f09\u60a8\u7684\u6a94\u6848\u3002",
                                        "\u624b\u673a\u5df2\u4e0b\u8f7d\u60a8\u7684\u6587\u4ef6\u3002"],
        ["Dialog.IncomingTransfer"]  = ["Incoming Transfer", "\u6536\u5230\u50b3\u8f38\u8acb\u6c42", "\u6536\u5230\u4f20\u8f93\u8bf7\u6c42"],
        ["Dialog.IncomingPrompt"]    = ["{0} wants to send {1} file(s) ({2}).\n\nAccept incoming transfer?",
                                        "{0} \u60f3\u8981\u50b3\u9001 {1} \u500b\u6a94\u6848 ({2})\u3002\n\n\u662f\u5426\u63a5\u53d7\u50b3\u8f38\uff1f",
                                        "{0} \u60f3\u8981\u53d1\u9001 {1} \u4e2a\u6587\u4ef6 ({2})\u3002\n\n\u662f\u5426\u63a5\u53d7\u4f20\u8f93\uff1f"],
        ["Dialog.AlreadyRunning"]    = ["CatShareSender is already running (check the system tray).",
                                        "CatShareSender \u5df2\u5728\u57f7\u884c\u4e2d\uff08\u8acb\u6aa2\u67e5\u7cfb\u7d71\u5323\uff09\u3002",
                                        "CatShareSender \u5df2\u5728\u8fd0\u884c\u4e2d\uff08\u8bf7\u68c0\u67e5\u7cfb\u7edf\u6258\u76d8\uff09\u3002"],
        ["Dialog.StillRunning"]      = ["Still running in the tray.",
                                        "\u4ecd\u5728\u7cfb\u7d71\u5323\u4e2d\u57f7\u884c\u3002",
                                        "\u4ecd\u5728\u7cfb\u7edf\u6258\u76d8\u4e2d\u8fd0\u884c\u3002"],

        // Tray menu
        ["Tray.Show"]           = ["Show",          "\u986f\u793a",          "\u663e\u793a"],
        ["Tray.Exit"]           = ["Exit",          "\u7d50\u675f",          "\u9000\u51fa"],

        // File dialog
        ["File.PickTitle"]      = ["Pick files to send", "\u9078\u64c7\u8981\u50b3\u9001\u7684\u6a94\u6848", "\u9009\u62e9\u8981\u53d1\u9001\u7684\u6587\u4ef6"],
        ["File.Summary"]        = ["Files: {0}   \u00b7   Size: {1}", "\u6a94\u6848\uff1a{0}   \u00b7   \u5927\u5c0f\uff1a{1}", "\u6587\u4ef6\uff1a{0}   \u00b7   \u5927\u5c0f\uff1a{1}"],
        ["File.Fallback"]       = ["file",          "\u6a94\u6848",          "\u6587\u4ef6"],

        // Device tile
        ["Device.Unknown"]      = ["Unknown device ({0})", "\u672a\u77e5\u88dd\u7f6e ({0})", "\u672a\u77e5\u8bbe\u5907 ({0})"],
        ["Device.SeenNow"]      = ["just now",      "\u525b\u624d",          "\u521a\u624d"],
        ["Device.SeenSeconds"]  = ["{0}s ago",      "{0}\u79d2\u524d",      "{0}\u79d2\u524d"],
        ["Device.SeenMinutes"]  = ["{0}m ago",      "{0}\u5206\u9418\u524d", "{0}\u5206\u949f\u524d"],
        ["Device.SeenHours"]    = ["{0}h ago",      "{0}\u5c0f\u6642\u524d", "{0}\u5c0f\u65f6\u524d"],

        // Mode dropdown
        ["Mode.Auto"]           = ["Auto (detect from phone)", "\u81ea\u52d5\uff08\u5f9e\u624b\u6a5f\u5075\u6e2c\uff09", "\u81ea\u52a8\uff08\u4ece\u624b\u673a\u68c0\u6d4b\uff09"],
        ["Mode.Alliance"]       = ["\u539f\u88dd\u4e92\u50b3 - iOS\u6a21\u64ec\u5340\u57df\u7db2\u8def", "\u539f\u88dd\u4e92\u50b3 - iOS\u6a21\u64ec\u5340\u57df\u7db2\u8def", "\u539f\u88c5\u4e92\u4f20 - iOS\u6a21\u62df\u5c40\u57df\u7f51"],
        ["Mode.CatShare"]       = ["CatShare - Hotspot mode", "CatShare - \u71b1\u9ede\u6a21\u5f0f", "CatShare - \u70ed\u70b9\u6a21\u5f0f"],

        // Engine messages (shown in UI status bar)
        ["Engine.NoLan"]        = ["No LAN adapter with a default gateway found \u2014 connect this PC to the same Wi-Fi router as the phone.",
                                   "\u672a\u627e\u5230\u5177\u6709\u9810\u8a2d\u9598\u9053\u7684\u5340\u57df\u7db2\u8def\u4ecb\u9762\u5361 \u2014 \u8acb\u5c07\u6b64\u96fb\u8166\u9023\u63a5\u5230\u8207\u624b\u6a5f\u76f8\u540c\u7684 Wi-Fi \u8def\u7531\u5668\u3002",
                                   "\u672a\u627e\u5230\u5177\u6709\u9ed8\u8ba4\u7f51\u5173\u7684\u5c40\u57df\u7f51\u9002\u914d\u5668 \u2014 \u8bf7\u5c06\u6b64\u7535\u8111\u8fde\u63a5\u5230\u4e0e\u624b\u673a\u76f8\u540c\u7684 Wi-Fi \u8def\u7531\u5668\u3002"],
        ["Engine.NoNetProfile"] = ["No network connection profile found \u2014 connect this laptop to Wi-Fi first.",
                                   "\u672a\u627e\u5230\u7db2\u8def\u9023\u7dda\u914d\u7f6e \u2014 \u8acb\u5148\u8b93\u7b46\u8a18\u672c\u9023\u4e0a Wi-Fi\u3002",
                                   "\u672a\u627e\u5230\u7f51\u7edc\u8fde\u63a5\u914d\u7f6e \u2014 \u8bf7\u5148\u8ba9\u7b14\u8bb0\u672c\u8fde\u4e0a Wi-Fi\u3002"],
        ["Engine.CatShareModeError"] = [
            "BLE: CatShare mode requires selecting a 'My Phone' (CatShare type) device \u2014 the current selection is an Alliance broadcast. Both entries come from two apps on the same phone; please pick the right one.",
            "BLE\uff1aCatShare \u6a21\u5f0f\u9700\u8981\u9078\u64c7\u300c\u6211\u7684\u624b\u6a5f\u300d\uff08CatShare \u985e\u578b\uff09\u7684\u88dd\u7f6e \u2014 \u7576\u524d\u9078\u4e2d\u7684\u662f\u539f\u88dd\u4e92\u50b3\u7684\u5ee3\u64ad\u3002\u5169\u689d\u88dd\u7f6e\u689d\u76ee\u4f86\u81ea\u540c\u4e00\u90e8\u624b\u6a5f\u4e0a\u7684\u5169\u500b App\uff0c\u8acb\u9078\u5c0d\u3002",
            "BLE\uff1aCatShare \u6a21\u5f0f\u9700\u8981\u9009\u62e9\u201c\u6211\u7684\u624b\u673a\u201d\uff08CatShare \u7c7b\u578b\uff09\u7684\u8bbe\u5907 \u2014 \u5f53\u524d\u9009\u4e2d\u7684\u662f\u539f\u88c5\u4e92\u4f20\u7684\u5e7f\u64ad\u3002\u4e24\u6761\u8bbe\u5907\u6761\u76ee\u6765\u81ea\u540c\u4e00\u90e8\u624b\u673a\u4e0a\u7684\u4e24\u4e2a App\uff0c\u8bf7\u9009\u5bf9\u3002"],
        ["Engine.StartingHotspot"] = ["starting Wi-Fi hotspot\u2026", "\u6b63\u5728\u555f\u52d5 Wi-Fi \u71b1\u9ede\u2026", "\u6b63\u5728\u542f\u52a8 Wi-Fi \u70ed\u70b9\u2026"],
        ["Engine.HotspotUp"]    = ["hotspot '{0}' up \u2014 the phone will switch Wi-Fi to it",
                                   "\u71b1\u9ede\u300c{0}\u300d\u5df2\u555f\u52d5 \u2014 \u624b\u6a5f\u5c07\u5207\u63db Wi-Fi \u9023\u7dda",
                                   "\u70ed\u70b9\u201c{0}\u201d\u5df2\u542f\u52a8 \u2014 \u624b\u673a\u5c06\u5207\u6362 Wi-Fi \u8fde\u63a5"],
        ["Engine.CredentialsSent"] = ["credentials sent to {0} \u2014 waiting for the phone to connect",
                                      "\u6191\u8b49\u5df2\u50b3\u9001\u81f3 {0} \u2014 \u7b49\u5f85\u624b\u6a5f\u9023\u7dda",
                                      "\u51ed\u636e\u5df2\u53d1\u9001\u81f3 {0} \u2014 \u7b49\u5f85\u624b\u673a\u8fde\u63a5"],
        ["Engine.BleHandshake"] = ["BLE handshake with {0}\u2026", "BLE \u8207 {0} \u9032\u884c\u63e1\u624b\u2026", "BLE \u4e0e {0} \u8fdb\u884c\u63e1\u624b\u2026"],
        ["Engine.OConnectCycle"]= ["OConnect cycle {0}/3: 9998/9896 handshake\u2026",
                                   "OConnect \u7b2c {0}/3 \u8f2a\uff1a9998/9896 \u63e1\u624b\u2026",
                                   "OConnect \u7b2c {0}/3 \u8f6e\uff1a9998/9896 \u63e1\u624b\u2026"],
    };
}

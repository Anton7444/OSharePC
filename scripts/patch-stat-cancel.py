from pathlib import Path

p = Path("TransferServer.cs")
s = p.read_text(encoding="utf-8")
old = '''                if (e.IsAction && e.Method == "status")
                {
                    var type = e.PayloadInt("type");
                    var reason = e.PayloadString("reason");
                    var taskId = e.PayloadString("taskId");
                    Log.Info($"WS: status type={type} reason='{reason}'");
                    _owner.OnStatus(taskId, type, reason);
                    await SendText(Envelope.Build("ack", e.Seq, "status", new { }));
                    // Stock OnePlus commonly sends success as type=1 with an empty reason.
                    if (type == 1) return "transfer completed";
                    if (type == 3) return $"refused: {reason}";
                }
                else if (e.IsAction)
                {
                    await SendText(Envelope.Build("ack", e.Seq, e.Method, new { }));
                }'''
new = '''                if (e.IsAction && (e.Method == "status" || e.Method == "stat"))
                {
                    // OnePlus phone-side Cancel sends action:*:stat and waits for
                    // ack:*:stat before closing with mCanceled=true.
                    var type = e.PayloadInt("type", int.MinValue);
                    if (type == int.MinValue) type = e.PayloadInt("status", int.MinValue);
                    var reason = e.PayloadString("reason");
                    if (string.IsNullOrWhiteSpace(reason)) reason = e.PayloadString("message");
                    var taskId = e.PayloadString("taskId");
                    if (string.IsNullOrWhiteSpace(taskId)) taskId = e.PayloadString("id");
                    Log.Info($"WS: {e.Method} type={(type == int.MinValue ? "?" : type)} reason='{reason}'");
                    await SendText(Envelope.Build("ack", e.Seq, e.Method, new { }));

                    if (e.Method == "stat")
                    {
                        if (type == 1)
                        {
                            _owner.OnStatus(taskId, 1, reason);
                            return "transfer completed";
                        }
                        var why = string.IsNullOrWhiteSpace(reason) ? "Phone cancelled the transfer." : reason;
                        _owner.ReportTransferFailure(taskId, why);
                        return $"phone cancelled: {why}";
                    }

                    _owner.OnStatus(taskId, type, reason);
                    if (type == 1) return "transfer completed";
                    if (type == 2) return string.IsNullOrWhiteSpace(reason) ? "transfer failed on phone" : $"transfer failed: {reason}";
                    if (type == 3) return string.IsNullOrWhiteSpace(reason) ? "phone refused transfer" : $"refused: {reason}";
                }
                else if (e.IsAction)
                {
                    await SendText(Envelope.Build("ack", e.Seq, e.Method, new { }));
                }'''
if old not in s:
    raise SystemExit("ReceiveLoop status block not found")
s = s.replace(old, new, 1)

old = '''        if (type == 1)
            MarkSuccessful(taskId);
        else if (type == 3)
            ReportTransferFailure(taskId, string.IsNullOrWhiteSpace(reason) ? "Phone refused the transfer." : reason);

        try { StatusReceived?.Invoke(taskId, type, reason); } catch { }
        if (type is 1 or 3)
            DisarmTransfer($"terminal status {type}: {reason}");'''
new = '''        if (type == 1)
            MarkSuccessful(taskId);
        else if (type == 2)
            ReportTransferFailure(taskId, string.IsNullOrWhiteSpace(reason) ? "Phone cancelled or failed the transfer." : reason);
        else if (type == 3)
            ReportTransferFailure(taskId, string.IsNullOrWhiteSpace(reason) ? "Phone refused the transfer." : reason);

        try { StatusReceived?.Invoke(taskId, type, reason); } catch { }
        if (type is 1 or 2 or 3)
            DisarmTransfer($"terminal status {type}: {reason}");'''
if old not in s:
    raise SystemExit("OnStatus block not found")
p.write_text(s.replace(old, new, 1), encoding="utf-8")

p = Path("Program.cs")
s = p.read_text(encoding="utf-8")
old = '''            if (args.Contains("--mockphone"))
            {
                var rc = RunMockPhone().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--zipprobe"))'''
new = '''            if (args.Contains("--mockphone"))
            {
                var rc = RunMockPhone().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--mockphone-cancel"))
            {
                var rc = RemoteCancelSelfTest.RunAsync().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--zipprobe"))'''
if old not in s:
    raise SystemExit("Program mockphone anchor not found")
p.write_text(s.replace(old, new, 1), encoding="utf-8")

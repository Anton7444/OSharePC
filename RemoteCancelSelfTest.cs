using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OShareSender;

internal static class RemoteCancelSelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        await using var server = new TransferServer();
        await server.StartAsync(SenderEngine.DefaultPort, configureFirewall: false);

        var tmp = Path.Combine(Path.GetTempPath(), "oshare-cancel-selftest.bin");
        await File.WriteAllBytesAsync(tmp, RandomNumberGenerator.GetBytes(4096));
        try
        {
            var task = new TransferTask
            {
                Files = new List<string> { tmp },
                SenderName = "OSharePC-MockCancel",
                SenderId = "0000",
            };
            task.ComputeSize();
            server.SetTask(task);
            server.ArmTransfer(task, IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString());

            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.TransferFailed += (id, reason) =>
            {
                if (id == task.TaskId) failed.TrySetResult(reason);
            };

            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{SenderEngine.DefaultPort}/websocket"), cts.Token);
            var buffer = new byte[8192];

            async Task<(Envelope? env, string text)> ReceiveAsync()
            {
                using var ms = new MemoryStream();
                while (true)
                {
                    var result = await ws.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("websocket closed");
                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    return (Envelope.Parse(text), text);
                }
            }

            async Task SendAsync(Envelope e) => await ws.SendAsync(
                Encoding.UTF8.GetBytes(e.ToString()), WebSocketMessageType.Text, true, cts.Token);

            var (vn, _) = await ReceiveAsync();
            if (vn is null || !vn.IsAction || vn.Method != "versionNegotiation")
                throw new InvalidOperationException("expected versionNegotiation");
            await SendAsync(new Envelope
            {
                Type = "ack",
                Seq = vn.Seq,
                Method = "versionNegotiation",
                Payload = JsonSerializer.SerializeToElement(new { version = 1, threadLimit = 5 }),
                HasPayload = true,
            });

            var (sendRequest, _) = await ReceiveAsync();
            if (sendRequest is null || !sendRequest.IsAction || sendRequest.Method != "sendRequest")
                throw new InvalidOperationException("expected sendRequest");
            await SendAsync(new Envelope
            {
                Type = "ack",
                Seq = sendRequest.Seq,
                Method = "sendRequest",
                Payload = JsonSerializer.SerializeToElement(new { }),
                HasPayload = true,
            });

            var (_, raw) = await ReceiveAsync();
            if (raw != "files")
            {
                Log.Error($"MOCKCANCEL FAIL: expected files trigger, got '{raw}'");
                failures++;
            }

            // Reproduce the terminal cancellation sequence seen in OnePlus logs:
            // receiver sends action:*:stat and expects ack:*:stat before closing with
            // mCanceled=true. A missing type is deliberately tested.
            await SendAsync(new Envelope
            {
                Type = "action",
                Seq = 77,
                Method = "stat",
                Payload = JsonSerializer.SerializeToElement(new { reason = "user interrupt" }),
                HasPayload = true,
            });

            var (ack, _) = await ReceiveAsync();
            if (ack is null || !ack.IsAck || ack.Seq != 77 || ack.Method != "stat")
            {
                Log.Error($"MOCKCANCEL FAIL: expected ack:77:stat, got {ack}");
                failures++;
            }

            string reason;
            try { reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { reason = ""; }
            if (reason != "user interrupt")
            {
                Log.Error($"MOCKCANCEL FAIL: expected TransferFailed('user interrupt'), got '{reason}'");
                failures++;
            }
            else
            {
                Log.Info("MOCKCANCEL: action:stat -> ack:stat -> TransferFailed verified");
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
            await server.StopAsync();
        }

        Log.Info(failures == 0 ? "MOCKCANCEL PASSED" : $"MOCKCANCEL FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}

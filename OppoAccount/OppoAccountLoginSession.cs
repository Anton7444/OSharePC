namespace OShareSender.OppoAccount;

/// <summary>
/// Drives one "log in to your OPPO account" attempt end-to-end and reports progress
/// via a push callback (matching OShareBridgeServer's event-queue convention), so the
/// Flutter settings page can show the QR code, live status, and finally the list of
/// available 2FA methods.
///
/// Covers only what's confirmed working: generate QR -> poll for scan/confirm ->
/// exchange for a processToken -> validate it -> list 2FA methods. Submitting an OTP
/// and the final native-side session exchange are not implemented yet (see
/// docs/oppo-account-login-notes.md).
/// </summary>
public sealed class OppoAccountLoginSession : IAsyncDisposable
{
    private readonly OppoAccountClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public void Start(Action<string, object> push)
    {
        _runTask = RunAsync(push, _cts.Token);
    }

    public void Cancel() => _cts.Cancel();

    private async Task RunAsync(Action<string, object> push, CancellationToken ct)
    {
        try
        {
            var qr = await _client.GenerateQrCodeAsync(ct);
            push("oppoAccountQr", new { qid = qr.Qid, qrcodeUrl = qr.QrcodeUrl });

            QrCodeStatus? status = null;
            for (var i = 0; i < 120; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                status = await _client.CheckQrCodeAsync(qr.Qid, ct);
                push("oppoAccountStatus", new { status = status.Status, accountName = status.AccountName });
                if (status.Status is "CONFIRMED" or "EXPIRED" or "CANCELLED") break;
            }

            if (status is null || status.Status != "CONFIRMED")
            {
                push("oppoAccountError", new { error = "Timed out waiting for the phone to confirm the QR code." });
                return;
            }

            var check = await _client.AuthnCheckAsync(qr.Qid, ct);
            var validate = await _client.AuthnValidateAsync(check.ProcessToken, ct);

            // envInfo's real expected content is still unconfirmed (see findings notes) —
            // "{}" is a best-effort placeholder. If the server rejects it, this surfaces
            // as oppoAccountError below rather than crashing.
            var methods = await _client.VerificationListAsync(check.ProcessToken, "{}", ct);

            push("oppoAccountMethods", new
            {
                accountName = validate.AccountName,
                verificationId = methods.VerificationId,
                currentRound = methods.CurrentRound,
                totalRound = methods.TotalRound,
                methods = methods.VerMethodList,
            });
        }
        catch (OperationCanceledException)
        {
            push("oppoAccountCancelled", new { });
        }
        catch (Exception ex)
        {
            push("oppoAccountError", new { error = ex.Message });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; } catch { /* already reported via push */ }
        }
        _cts.Dispose();
        _client.Dispose();
    }
}

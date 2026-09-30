using System.Text.Json;

namespace OShareSender.OppoAccount;

/// <summary>
/// Drives one "log in to your OPPO account" attempt end-to-end and reports progress
/// via a push callback (matching OShareBridgeServer's event-queue convention), so the
/// Flutter settings page can show the QR code, live status, and the available 2FA
/// methods.
///
/// Covers the confirmed web flow: generate QR -> poll for scan/confirm -> exchange for
/// a processToken -> validate it -> list methods -> gather/validate the selected
/// password or one-time code. The final native-side session exchange is still not
/// implemented (see docs/oppo-account-login-notes.md).
/// </summary>
public sealed class OppoAccountLoginSession : IAsyncDisposable
{
    private readonly OppoAccountClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private string? _processToken;
    private IReadOnlyList<VerificationMethod> _verificationMethods = Array.Empty<VerificationMethod>();
    private Task? _runTask;

    private const string VerificationSceneId = "RYhhvBD6i3zDrCL3ARei";

    public void Start(Action<string, object> push)
    {
        _runTask = RunAsync(push, _cts.Token);
    }

    public void Cancel() => _cts.Cancel();

    public async Task<VerificationGatherResult> GatherVerificationAsync(string verMethod)
    {
        var processToken = _processToken;
        if (string.IsNullOrWhiteSpace(processToken))
            throw new InvalidOperationException("The OPPO login has not reached two-factor verification yet.");
        EnsureKnownVerificationMethod(verMethod);
        return await _client.GatherUserDataAsync(processToken, verMethod, ct: _cts.Token);
    }

    public async Task<VerificationValidationResult> ValidateVerificationAsync(
        string verMethod,
        string validateData)
    {
        var processToken = _processToken;
        if (string.IsNullOrWhiteSpace(processToken))
            throw new InvalidOperationException("The OPPO login has not reached two-factor verification yet.");
        if (string.IsNullOrWhiteSpace(validateData))
            throw new ArgumentException("A verification code or password is required.", nameof(validateData));
        EnsureKnownVerificationMethod(verMethod);

        return await _client.ValidateUserDataAsync(processToken, verMethod, validateData, _cts.Token);
    }

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
                if (!string.IsNullOrWhiteSpace(status.AccountName))
                    SettingsStore.Save(oppoAccountName: status.AccountName);
                if (status.Status is "CONFIRMED" or "EXPIRED" or "CANCELLED") break;
            }

            if (status is null || status.Status != "CONFIRMED")
            {
                push("oppoAccountError", new { error = "Timed out waiting for the phone to confirm the QR code." });
                return;
            }

            var check = await _client.AuthnCheckAsync(qr.Qid, ct);
            var validate = await _client.AuthnValidateAsync(check.ProcessToken, ct);
            if (!string.IsNullOrWhiteSpace(validate.AccountName))
                SettingsStore.Save(oppoAccountName: validate.AccountName);

            // The web client sends a JSON string here, rather than an empty object.
            // Sending the same shape avoids a server-side validation error before the
            // actual 2FA methods are returned.
            var envInfo = JsonSerializer.Serialize(new
            {
                sceneId = ExtractSceneId(validate.VerificationUrl) ?? VerificationSceneId,
                thirdPartyAppInfo = "",
                enableFinger = false,
                enablePin = false,
            });
            var methods = await _client.VerificationListAsync(check.ProcessToken, envInfo, ct);
            _processToken = methods.ProcessToken;
            _verificationMethods = methods.VerMethodList;

            push("oppoAccountMethods", new
            {
                accountName = validate.AccountName,
                verificationId = methods.VerificationId,
                currentRound = methods.CurrentRound,
                totalRound = methods.TotalRound,
                tokenExpired = methods.TokenExpired,
                methods = methods.VerMethodList.Select(m => m.VerMethod).ToArray(),
                methodDetails = methods.VerMethodList.Select(m => new
                {
                    method = m.VerMethod,
                    display = m.Display,
                    order = m.Order,
                }).ToArray(),
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

    private void EnsureKnownVerificationMethod(string verMethod)
    {
        if (string.IsNullOrWhiteSpace(verMethod) ||
            !_verificationMethods.Any(m => string.Equals(m.VerMethod, verMethod, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That verification method is not available for this login.", nameof(verMethod));
    }

    private static string? ExtractSceneId(string? verificationUrl)
    {
        if (string.IsNullOrWhiteSpace(verificationUrl)) return null;
        var queryStart = verificationUrl.IndexOf('?');
        if (queryStart < 0 || queryStart == verificationUrl.Length - 1) return null;
        foreach (var part in verificationUrl[(queryStart + 1)..].Split('&'))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && string.Equals(pieces[0], "sceneId", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pieces[1]);
        }
        return null;
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

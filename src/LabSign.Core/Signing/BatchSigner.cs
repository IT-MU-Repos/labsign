using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using iText.Signatures;
using LabSign.Core.Token;

namespace LabSign.Core.Signing;

/// <summary>
/// Signs a batch the way SignReports.vb moveAndUpdateSelected does: .pdf signed into Signed\,
/// .xlsx copied there, anything else left in Pending. The Pending originals are never deleted here;
/// that is <see cref="Finalize"/>, called only after Django has recorded the batch.
/// </summary>
public sealed class BatchSigner(
    LabSignConfig config,
    ITokenProvider token,
    ISignerInteraction interaction,
    SigningGate gate)
{
    public const int MaxPinAttempts = PinLogin.MaxAttempts;

    public BatchSigner(LabSignConfig config, ITokenProvider token, ISignerInteraction interaction)
        : this(config, token, interaction, new SigningGate())
    {
    }

    public bool IsBusy => gate.IsBusy;

    /// <summary>Validates every path before anything is touched; the first problem rejects the batch.</summary>
    public static string? Validate(SignBatchRequest request)
    {
        if (request.Files.Count == 0) return "No files to sign.";
        if (string.IsNullOrWhiteSpace(request.SignerName)) return "signer_name is required.";
        foreach (var file in request.Files)
        {
            if (!ReportPaths.TryValidate(file.Filename, file.PendingPath, file.SignedPath, out var error))
                return error;
        }
        return null;
    }

    public bool TryBegin() => gate.TryEnter();

    /// <summary>Runs a batch already admitted by <see cref="TryBegin"/>, releasing it when done.</summary>
    public async IAsyncEnumerable<BatchEvent> RunAsync(
        SignBatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return new BatchEvent { Type = "start", Total = request.Files.Count };

            var needsToken = request.Files.Any(f => IsPdf(f.Filename) && File.Exists(f.PendingPath));
            ITokenSession? session = null;
            if (needsToken)
            {
                var status = token.GetStatus();
                if (!status.Present)
                {
                    yield return Error(status.Error ?? "No SafeNet token found.");
                    yield break;
                }
                yield return new BatchEvent { Type = "pin_required", TokenLabel = status.Label };

                var pdfCount = request.Files.Count(f => IsPdf(f.Filename));
                var (loggedIn, error) = await PinLogin.LoginAsync(token, interaction, status, pdfCount, cancellationToken);
                if (loggedIn is null)
                {
                    yield return Error(error ?? "Could not log in to the token.");
                    yield break;
                }
                session = loggedIn;
            }

            using (session)
            {
                var signedAt = DateTime.Now;
                var stamp = new StampContent(
                    interaction.RenderStamp(request.SignerName, signedAt),
                    $"Digitally signed by: {request.SignerName}\nDate: {signedAt:yyyy-MM-dd HH:mm}");
                var reason = string.IsNullOrWhiteSpace(request.Reason) ? config.DefaultReason : request.Reason!;
                var tsa = string.IsNullOrWhiteSpace(config.TsaUrl) ? null : new TSAClientBouncyCastle(config.TsaUrl);

                int succeeded = 0, failed = 0;
                foreach (var file in request.Files)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var result = await Task.Run(() => SignOne(file, session, stamp, reason, tsa), CancellationToken.None);
                    if (FileStatus.IsSuccess(result.Status!)) succeeded++; else failed++;
                    Log.Write($"{result.Filename}: {result.Status}{(result.Error is null ? "" : $" ({result.Error})")}");
                    yield return result;
                }
                if (session is not null)
                    Log.Write($"Batch by {request.SignerName} with {session.Certificate.Subject}: {succeeded} ok, {failed} failed");
                yield return new BatchEvent
                {
                    Type = "done",
                    Succeeded = succeeded,
                    Failed = failed,
                    CertificateSubject = session?.Certificate.Subject,
                };
            }
        }
        finally
        {
            gate.Exit();
        }
    }

    private BatchEvent SignOne(ReportFile file, ITokenSession? session, StampContent stamp, string reason, ITSAClient? tsa)
    {
        try
        {
            if (!File.Exists(file.PendingPath))
            {
                // SignReports.vb signThePDFToSigned: an earlier run got it into Signed but failed
                // before the database update, so it is listed again with nothing left in Pending.
                return File.Exists(file.SignedPath)
                    ? FileEvent(file, FileStatus.AlreadySigned, file.SignedPath)
                    : FileEvent(file, FileStatus.Failed, error: "Not found in Pending or Signed.");
            }

            if (IsPdf(file.Filename))
            {
                var marker = SignHereFinder.Find(file.PendingPath);
                if (marker is null)
                    return FileEvent(file, FileStatus.Failed, error: $"\"{SignHereFinder.Marker}\" not found in the file.");
                PdfReportSigner.Sign(file.PendingPath, file.SignedPath, marker, stamp, reason, config.Location,
                    session!.Signature, session.Chain, tsa);
                return FileEvent(file, FileStatus.Signed, file.SignedPath);
            }

            if (Path.GetExtension(file.Filename).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file.PendingPath, file.SignedPath, overwrite: true);
                return FileEvent(file, FileStatus.Copied, file.SignedPath);
            }

            return FileEvent(file, FileStatus.Skipped, file.PendingPath);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return FileEvent(file, FileStatus.Failed, error: "The file is open in another program. Close it and sign again.");
        }
        catch (Exception ex)
        {
            return FileEvent(file, FileStatus.Failed, error: ex.Message);
        }
    }

    /// <summary>
    /// Deletes a Pending original only when its Signed twin exists. A skipped file (no twin) stays
    /// in Pending, which is where legacy leaves it.
    /// </summary>
    public static List<FinalizeResult> Finalize(FinalizeRequest request)
    {
        var results = new List<FinalizeResult>();
        foreach (var file in request.Files)
        {
            if (!ReportPaths.TryValidate(file.Filename, file.PendingPath, file.SignedPath, out var error))
            {
                results.Add(new FinalizeResult { Filename = file.Filename, Error = error });
                continue;
            }
            try
            {
                var delete = File.Exists(file.PendingPath) && File.Exists(file.SignedPath);
                if (delete) File.Delete(file.PendingPath);
                results.Add(new FinalizeResult { Filename = file.Filename, Deleted = delete });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new FinalizeResult
                {
                    Filename = file.Filename,
                    Error = IsSharingViolation(ex) ? "The file is open in another program." : ex.Message,
                });
            }
        }
        return results;
    }

    private static bool IsPdf(string filename) =>
        Path.GetExtension(filename).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33), surfaced as HResult 0x80070020/21.
    private static bool IsSharingViolation(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static BatchEvent FileEvent(ReportFile file, string status, string? fullPath = null, string? error = null) => new()
    {
        Type = "file",
        Filename = file.Filename,
        Status = status,
        FullPath = fullPath,
        Sha256 = (status is FileStatus.Signed or FileStatus.Copied or FileStatus.AlreadySigned) && fullPath is not null
            ?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant()
            : null,
        Error = error,
    };

    private static BatchEvent Error(string message) => new() { Type = "error", Error = message };
}

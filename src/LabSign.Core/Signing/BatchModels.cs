using System.Text.Json.Serialization;

namespace LabSign.Core.Signing;

public sealed class ReportFile
{
    public string Filename { get; set; } = "";
    public string PendingPath { get; set; } = "";
    public string SignedPath { get; set; } = "";
}

public sealed class SignBatchRequest
{
    public string SignerName { get; set; } = "";
    public string? Reason { get; set; }
    public List<ReportFile> Files { get; set; } = [];
}

public sealed class FinalizeRequest
{
    public List<ReportFile> Files { get; set; } = [];
}

/// <summary>What happened to one file. <c>full_path</c> is what Django stores in otherfiles.fullpath.</summary>
public static class FileStatus
{
    public const string Signed = "signed";
    /// <summary>An .xlsx: copied to Signed unsigned, as legacy does.</summary>
    public const string Copied = "copied";
    /// <summary>Any other type: nothing written, it stays in Pending but is still marked signed.</summary>
    public const string Skipped = "skipped";
    /// <summary>An earlier run signed it but never recorded that; it is already in Signed.</summary>
    public const string AlreadySigned = "already_signed";
    public const string Failed = "failed";

    public static bool IsSuccess(string status) => status != Failed;
}

/// <summary>
/// One NDJSON line of the /sign-batch stream. <c>type</c> is
/// <c>start</c> → (<c>pin_required</c>) → <c>file</c>… → <c>done</c>, or <c>error</c> to end early.
/// </summary>
public sealed class BatchEvent
{
    public string Type { get; init; } = "";
    public int? Total { get; init; }
    public string? TokenLabel { get; init; }
    public string? CertificateSubject { get; init; }
    public string? Filename { get; init; }
    public string? Status { get; init; }
    public string? FullPath { get; init; }
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
    public int? Succeeded { get; init; }
    public int? Failed { get; init; }

    [JsonIgnore]
    public bool IsTerminal => Type is "done" or "error";
}

public sealed class FinalizeResult
{
    public string Filename { get; init; } = "";
    public bool Deleted { get; init; }
    public string? Error { get; init; }
}

using LabSign.Core;
using LabSign.Core.Signing;

namespace LabSign.Tests;

public class PdfReportSignerTests
{
    [Fact]
    public void Finds_the_marker_and_signs_a_valid_whole_document_signature()
    {
        using var tree = new ReportTree();
        var file = tree.AddPdf("L1.pdf");
        var certificate = TestCertificate.Create();

        var marker = SignHereFinder.Find(file.PendingPath);
        Assert.NotNull(marker);
        Assert.Equal(1, marker.PageNumber);

        PdfReportSigner.Sign(file.PendingPath, file.SignedPath, marker, new StampContent(null, "Digitally signed by: Test"),
            "Report approved", "", new SoftwareSignature(certificate.Key), [certificate.ITextCertificate], tsa: null);

        var (covers, intact, subject) = TestPdfs.Verify(file.SignedPath);
        Assert.True(covers);
        Assert.True(intact);
        Assert.Contains("Test Signatory", subject);
        Assert.Empty(Directory.GetFiles(tree.Signed, "*.labsign-partial", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_file_without_the_marker_has_no_position() =>
        Assert.Null(SignHereFinder.Find(new ReportTree().AddPdf("L2.pdf", withMarker: false).PendingPath));

    [Fact]
    public void Placement_matches_the_legacy_rectangle()
    {
        // SignReports.vb: (round(left), round(top) - 58); Signing.vb: Rectangle(X, Y + 50, 200, 65).
        var rect = PdfReportSigner.Placement(new SignHereMarker(1, 36.4, 700.6));
        Assert.Equal(36f, rect.GetX());
        Assert.Equal(701f - 58 + 50, rect.GetY());
        Assert.Equal(200f, rect.GetWidth());
        Assert.Equal(65f, rect.GetHeight());
    }
}

public class BatchSignerTests
{
    private static readonly LabSignConfig Config = new();

    private static async Task<List<BatchEvent>> Run(BatchSigner signer, SignBatchRequest request)
    {
        Assert.True(signer.TryBegin());
        var events = new List<BatchEvent>();
        await foreach (var e in signer.RunAsync(request, CancellationToken.None)) events.Add(e);
        Assert.False(signer.IsBusy);
        return events;
    }

    private static SignBatchRequest Request(params ReportFile[] files) =>
        new() { SignerName = "Test Signatory", Files = [.. files] };

    [Fact]
    public async Task Handles_every_kind_of_file_like_legacy()
    {
        using var tree = new ReportTree();
        var pdf = tree.AddPdf("L1.pdf");
        var xlsx = tree.AddBytes("L1.xlsx");
        var other = tree.AddBytes("L1.docx");
        var noMarker = tree.AddPdf("L2.pdf", withMarker: false);
        var alreadySigned = tree.AddBytes("L3.pdf", folder: "Signed");
        var missing = tree.File("L4.pdf");
        var token = new FakeTokenProvider();
        var signer = new BatchSigner(Config, token, new ScriptedInteraction("1234"));

        var events = await Run(signer, Request(pdf, xlsx, other, noMarker, alreadySigned, missing));

        Assert.Equal(["start", "pin_required", "file", "file", "file", "file", "file", "file", "done"], events.Select(e => e.Type));
        var byName = events.Where(e => e.Type == "file").ToDictionary(e => e.Filename!);
        Assert.Equal(FileStatus.Signed, byName["L1.pdf"].Status);
        Assert.Equal(pdf.SignedPath, byName["L1.pdf"].FullPath);
        Assert.NotNull(byName["L1.pdf"].Sha256);
        Assert.Equal(FileStatus.Copied, byName["L1.xlsx"].Status);
        Assert.Equal(FileStatus.Skipped, byName["L1.docx"].Status);
        Assert.Equal(other.PendingPath, byName["L1.docx"].FullPath);
        Assert.Equal(FileStatus.Failed, byName["L2.pdf"].Status);
        Assert.Contains("SignHere!", byName["L2.pdf"].Error);
        Assert.Equal(FileStatus.AlreadySigned, byName["L3.pdf"].Status);
        Assert.Equal(FileStatus.Failed, byName["L4.pdf"].Status);
        Assert.Equal(4, events[^1].Succeeded);
        Assert.Equal(2, events[^1].Failed);

        Assert.True(TestPdfs.Verify(pdf.SignedPath).Intact);
        // Nothing leaves Pending until Django has recorded the batch.
        Assert.True(File.Exists(pdf.PendingPath));
        Assert.True(File.Exists(xlsx.PendingPath));
        Assert.True(token.LastSession!.Disposed);
    }

    [Fact]
    public async Task A_batch_without_pdfs_to_sign_never_asks_for_the_pin()
    {
        using var tree = new ReportTree();
        var interaction = new ScriptedInteraction();
        var events = await Run(new BatchSigner(Config, new FakeTokenProvider(), interaction), Request(tree.AddBytes("L1.xlsx")));

        Assert.Empty(interaction.Requests);
        Assert.Equal(FileStatus.Copied, events.Single(e => e.Type == "file").Status);
    }

    [Fact]
    public async Task Cancelling_the_pin_signs_nothing()
    {
        using var tree = new ReportTree();
        var pdf = tree.AddPdf("L1.pdf");
        var events = await Run(new BatchSigner(Config, new FakeTokenProvider(), new ScriptedInteraction()), Request(pdf));

        Assert.Equal("error", events[^1].Type);
        Assert.DoesNotContain(events, e => e.Type == "file");
        Assert.False(File.Exists(pdf.SignedPath));
    }

    [Fact]
    public async Task A_wrong_pin_is_asked_again_with_a_warning()
    {
        using var tree = new ReportTree();
        var token = new FakeTokenProvider();
        var interaction = new ScriptedInteraction("0000", "1234");
        var events = await Run(new BatchSigner(Config, token, interaction), Request(tree.AddPdf("L1.pdf")));

        Assert.Equal(["0000", "1234"], token.PinsTried);
        Assert.Null(interaction.Requests[0].Problem);
        Assert.StartsWith("Wrong PIN", interaction.Requests[1].Problem);
        Assert.Equal("done", events[^1].Type);
    }

    [Fact]
    public async Task Gives_up_after_three_wrong_pins()
    {
        using var tree = new ReportTree();
        var token = new FakeTokenProvider();
        var events = await Run(new BatchSigner(Config, token, new ScriptedInteraction("1", "2", "3", "1234")), Request(tree.AddPdf("L1.pdf")));

        Assert.Equal(BatchSigner.MaxPinAttempts, token.PinsTried.Count);
        Assert.Equal("error", events[^1].Type);
    }

    [Fact]
    public async Task No_token_ends_the_batch_before_the_pin()
    {
        using var tree = new ReportTree();
        var interaction = new ScriptedInteraction("1234");
        var events = await Run(new BatchSigner(Config, new FakeTokenProvider(present: false), interaction), Request(tree.AddPdf("L1.pdf")));

        Assert.Empty(interaction.Requests);
        Assert.Contains("token", events[^1].Error);
    }

    [Fact]
    public void Finalize_deletes_pending_only_when_the_signed_twin_exists()
    {
        using var tree = new ReportTree();
        var signed = tree.AddBytes("L1.pdf");
        File.WriteAllText(signed.SignedPath, "signed");
        var skipped = tree.AddBytes("L1.docx");

        var results = BatchSigner.Finalize(new FinalizeRequest { Files = [signed, skipped] });

        Assert.True(results[0].Deleted);
        Assert.False(File.Exists(signed.PendingPath));
        Assert.False(results[1].Deleted);
        Assert.True(File.Exists(skipped.PendingPath));
    }

    [Fact]
    public void Validate_rejects_the_whole_batch_on_one_bad_path()
    {
        using var tree = new ReportTree();
        var bad = tree.File("L2.pdf");
        bad.SignedPath = "/etc/L2.pdf";
        Assert.NotNull(BatchSigner.Validate(Request(tree.File("L1.pdf"), bad)));
    }
}

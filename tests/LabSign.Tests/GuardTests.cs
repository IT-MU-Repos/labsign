using LabSign.Core;

namespace LabSign.Tests;

public class ReportPathsTests
{
    private const string Pending = @"\\10.0.0.5\Lab-Data\Reports\Leaf\Pending\L123.pdf";
    private const string Signed = @"\\10.0.0.5\Lab-Data\Reports\Leaf\Signed\L123.pdf";

    [Fact]
    public void Accepts_a_pending_file_and_its_signed_sibling() =>
        Assert.True(ReportPaths.TryValidate("L123.pdf", Pending, Signed, out _));

    [Fact]
    public void Folder_names_are_case_insensitive_like_windows() =>
        Assert.True(ReportPaths.TryValidate("L123.pdf",
            @"\\10.0.0.5\lab-data\reports\leaf\PENDING\L123.pdf", @"\\10.0.0.5\Lab-Data\Reports\Leaf\signed\l123.pdf", out _));

    [Theory]
    [InlineData(@"\\10.0.0.5\Lab-Data\Reports\Leaf\Sent\L123.pdf", Signed)]
    [InlineData(@"C:\Windows\System32\L123.pdf", Signed)]
    [InlineData(Pending, @"\\10.0.0.5\Lab-Data\Reports\Soil\Signed\L123.pdf")]
    [InlineData(Pending, @"\\10.0.0.6\Lab-Data\Reports\Leaf\Signed\L123.pdf")]
    [InlineData(Pending, @"\\10.0.0.5\Lab-Data\Reports\Leaf\Signed\..\..\..\Windows\L123.pdf")]
    [InlineData(@"\\10.0.0.5\Lab-Data\Reports\Leaf\Pending\.\L123.pdf", Signed)]
    [InlineData(Pending, Pending)]
    public void Rejects_anything_but_the_pending_to_signed_pair(string pending, string signed) =>
        Assert.False(ReportPaths.TryValidate("L123.pdf", pending, signed, out _));

    [Theory]
    [InlineData("other.pdf")]
    [InlineData(@"..\L123.pdf")]
    [InlineData("a/b.pdf")]
    [InlineData("")]
    [InlineData(" L123.pdf")]
    public void The_filename_must_be_the_plain_last_segment(string filename) =>
        Assert.False(ReportPaths.TryValidate(filename, Pending, Signed, out _));
}

public class RequestGuardsTests
{
    private static readonly string[] Allowed = ["https://labserve.net", "http://localhost:5010"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://labserve.net")]
    [InlineData("HTTP://LOCALHOST:5010")]
    public void Allows_no_origin_or_a_listed_one(string? origin) =>
        Assert.True(RequestGuards.IsOriginAllowed(origin, Allowed));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://labserve.net.evil.example")]
    [InlineData("null")]
    public void Rejects_other_origins(string origin) =>
        Assert.False(RequestGuards.IsOriginAllowed(origin, Allowed));

    [Theory]
    [InlineData("127.0.0.1:5578", true)]
    [InlineData("localhost:5578", true)]
    [InlineData("localhost", true)]
    [InlineData("[::1]:5578", true)]
    [InlineData("evil.example:5578", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("", false)]
    public void Only_loopback_host_names_are_served(string host, bool expected) =>
        Assert.Equal(expected, RequestGuards.IsHostAllowed(host));
}

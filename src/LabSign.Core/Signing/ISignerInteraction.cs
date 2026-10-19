namespace LabSign.Core.Signing;

public sealed record PinRequest(string TokenLabel, int PdfCount, string? Problem);

/// <summary>The parts that need a desktop: asking for the PIN and drawing the stamp image.</summary>
public interface ISignerInteraction
{
    /// <summary>The PIN, or null if the user cancelled.</summary>
    Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken);

    /// <summary>A PNG stamp, or null to fall back to a text-only appearance.</summary>
    byte[]? RenderStamp(string signerName, DateTime signedAt);
}

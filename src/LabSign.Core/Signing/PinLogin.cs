using LabSign.Core.Token;

namespace LabSign.Core.Signing;

/// <summary>
/// Asking for the PIN and logging in, shared by the share batch and byte-signing sessions:
/// up to <see cref="MaxAttempts"/> tries, re-asking only after a wrong PIN, since anything else
/// (locked PIN, token pulled, driver missing) will not be cured by asking again.
/// </summary>
public static class PinLogin
{
    public const int MaxAttempts = 3;

    public static async Task<(ITokenSession? Session, string? Error)> LoginAsync(
        ITokenProvider token,
        ISignerInteraction interaction,
        TokenStatus status,
        int documentCount,
        CancellationToken cancellationToken)
    {
        string? problem = null;
        for (var attempt = 1; ; attempt++)
        {
            var pin = await interaction.RequestPinAsync(
                new PinRequest(status.Label ?? "token", documentCount, problem), cancellationToken);
            if (pin is null) return (null, "PIN entry was cancelled. Nothing was signed.");

            try
            {
                return (token.Login(pin), null);
            }
            catch (TokenException ex) when (ex.Kind == TokenErrorKind.WrongPin && attempt < MaxAttempts)
            {
                problem = "Wrong PIN. Too many wrong attempts lock the token.";
            }
            catch (TokenException ex)
            {
                return (null, ex.Message);
            }
            catch (Exception ex)
            {
                return (null, $"Could not log in to the token: {ex.Message}");
            }
        }
    }
}

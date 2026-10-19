namespace LabSign.Core.Signing;

/// <summary>
/// One signing job at a time across the whole app: a share batch (LIMS) or a byte-signing
/// session (Mule). The token is one device with one login state; two jobs interleaving their
/// logins and logouts would sign with, or log out from under, each other.
/// </summary>
public sealed class SigningGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsBusy => _semaphore.CurrentCount == 0;

    public bool TryEnter() => _semaphore.Wait(0);

    public void Exit() => _semaphore.Release();
}

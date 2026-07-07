using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace Wisp;

/// <summary>Windows Hello / PIN gate for sensitive actions (e.g. revealing a saved password).</summary>
public static class OsAuth
{
    /// <summary>Prompts for Windows Hello / PIN and returns true if the user verified. If the device
    /// has no Hello configured (or WinRT is unavailable), returns true so a user isn't locked out of
    /// their own passwords — matching how Chromium behaves when no OS auth is present.</summary>
    public static async Task<bool> VerifyAsync(string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
                return true; // Hello not set up on this PC — don't block
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch { return true; } // WinRT/consent API unavailable — fail open
    }
}

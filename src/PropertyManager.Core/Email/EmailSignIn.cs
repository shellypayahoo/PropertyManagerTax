using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace PropertyManager.Core.Email;

/// <summary>
/// Signs in to the mailbox with Microsoft (device code flow, read-only Mail.Read and Calendars.Read permissions). The sign-in is kept in
/// the macOS Keychain (Windows: DPAPI) so it survives restarts; the app never sees the password.
/// </summary>
public class EmailSignIn(IOptions<EmailOptions> options) : IMailTokenProvider
{
    private static readonly string[] Scopes = ["Mail.Read", "Calendars.Read"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPublicClientApplication? app;

    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>The signed-in address, or null.</summary>
    public async Task<string?> GetSignedInAddressAsync()
    {
        if (!IsConfigured)
        {
            return null;
        }
        var accounts = await (await AppAsync()).GetAccountsAsync();
        return accounts.FirstOrDefault()?.Username;
    }

    /// <summary>
    /// Starts signing in. <paramref name="showCode"/> receives the web address and code to enter; the task finishes once
    /// the owner has signed in there.
    /// </summary>
    public async Task<string> SignInAsync(Func<string, string, Task> showCode, CancellationToken ct)
    {
        var result = await (await AppAsync())
            .AcquireTokenWithDeviceCode(Scopes, code => showCode(code.VerificationUrl, code.UserCode))
            .ExecuteAsync(ct);
        return result.Account.Username;
    }

    /// <summary>
    /// True when signed in but the sign-in doesn't cover every permission the app now uses (e.g. it was made before
    /// calendar access was added), so the owner needs to sign in again once.
    /// </summary>
    public async Task<bool> NeedsSignInAgainAsync(CancellationToken ct = default)
    {
        var client = await AppAsync();
        if ((await client.GetAccountsAsync()).FirstOrDefault() is not { } account)
        {
            return false;
        }
        try
        {
            await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return false;
        }
        catch (MsalUiRequiredException)
        {
            return true;
        }
    }

    public async Task SignOutAsync()
    {
        var client = await AppAsync();
        foreach (var account in await client.GetAccountsAsync())
        {
            await client.RemoveAsync(account);
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var client = await AppAsync();
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new InvalidOperationException("Not signed in to email. Sign in on the Email page.");
        try
        {
            return (await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            throw new InvalidOperationException("The email sign-in has expired. Sign in again on the Email page.");
        }
    }

    private async Task<IPublicClientApplication> AppAsync()
    {
        if (app is not null)
        {
            return app;
        }
        await gate.WaitAsync();
        try
        {
            if (app is null)
            {
                if (!IsConfigured)
                {
                    throw new InvalidOperationException("Email isn't set up: add the app registration's client id (see the README).");
                }
                var client = PublicClientApplicationBuilder.Create(options.Value.ClientId)
                    .WithAuthority(AzureCloudInstance.AzurePublic, options.Value.Tenant)
                    .WithDefaultRedirectUri()
                    .Build();
                await PersistCacheAsync(client);
                app = client;
            }
            return app;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task PersistCacheAsync(IPublicClientApplication client)
    {
        var folder = Path.Combine(MsalCacheHelper.UserRootDirectory, ".propertymanager");
        var storage = new StorageCreationPropertiesBuilder("email-signin.cache", folder)
            .WithMacKeyChain("PropertyManager.EmailSignIn", "MSALCache")
            .WithLinuxUnprotectedFile()
            .Build();
        try
        {
            var helper = await MsalCacheHelper.CreateAsync(storage);
            helper.VerifyPersistence();
            helper.RegisterCache(client.UserTokenCache);
        }
        catch (MsalCachePersistenceException)
        {
            // Keychain unavailable: the sign-in lasts until the app stops.
        }
    }
}

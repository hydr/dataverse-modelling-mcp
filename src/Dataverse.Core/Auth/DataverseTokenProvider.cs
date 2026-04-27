namespace Dataverse.Core.Auth;

using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

/// <summary>
/// Provides OAuth2 tokens via MSAL PublicClientApplication.
/// Tries silent acquisition first; falls back to interactive browser login.
/// </summary>
public sealed class DataverseTokenProvider : ITokenProvider
{
    private readonly ILogger<DataverseTokenProvider> _logger;
    private IPublicClientApplication? _app;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Loaded from config at first use
    private string? _clientId;
    private string? _tenantId;

    public DataverseTokenProvider(ILogger<DataverseTokenProvider> logger)
    {
        _logger = logger;
    }

    public void Configure(string clientId, string? tenantId)
    {
        _clientId = clientId;
        _tenantId = tenantId;
    }

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        var app = await GetOrCreateAppAsync(ct);
        var scopes = new[] { scope };

        // Try all cached accounts first
        var accounts = await app.GetAccountsAsync();
        foreach (var account in accounts)
        {
            try
            {
                var silentResult = await app
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(ct);
                return silentResult.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // This account needs re-auth; fall through to interactive
            }
        }

        _logger.LogInformation("Silent token acquisition failed — opening browser for interactive login");

        var interactiveResult = await app
            .AcquireTokenInteractive(scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(ct);

        return interactiveResult.AccessToken;
    }

    private async Task<IPublicClientApplication> GetOrCreateAppAsync(CancellationToken ct)
    {
        if (_app is not null)
            return _app;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_app is not null)
                return _app;

            if (string.IsNullOrEmpty(_clientId))
                throw new InvalidOperationException(
                    "DataverseTokenProvider is not configured. Call Configure(clientId, tenantId) first.");

            var builder = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithRedirectUri("http://localhost");

            if (!string.IsNullOrEmpty(_tenantId))
                builder = builder.WithTenantId(_tenantId);
            else
                builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs);

            var app = builder.Build();

            // Register the persistent cache
            var cacheHelper = await SecureTokenCache.CreateAsync();
            cacheHelper.RegisterCache(app.UserTokenCache);

            _app = app;
            return _app;
        }
        finally
        {
            _initLock.Release();
        }
    }
}

namespace Dataverse.Core.Auth;

using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

/// <summary>
/// Provides OAuth2 tokens via MSAL PublicClientApplication.
/// Tries silent acquisition first; falls back to interactive browser login.
/// Also offers a second token flow scoped to the Power-Apps-Maker AppId
/// (<c>a8f7a65c-f5ba-4859-b2d6-df772c264e9d</c>) — required for Pipeline-Backend
/// mutating endpoints, which filter by the token's <c>appid</c> claim.
/// </summary>
public sealed class DataverseTokenProvider : ITokenProvider
{
    /// <summary>
    /// Microsoft First-Party App ID for "Power Apps Maker portal" — the one Pipeline-Backend
    /// accepts as a trigger source. MSAL FOCI lets PublicClientApplication acquire tokens
    /// for this AppId from the user's existing cached refresh-token, no app registration needed.
    /// </summary>
    public const string PowerAppsMakerClientId = "a8f7a65c-f5ba-4859-b2d6-df772c264e9d";

    /// <summary>
    /// Power Platform CLI (PAC) AppId — public client that AAD accepts as device-code initiator
    /// without demanding a client secret. Extracted from <c>bolt.authentication.dll</c> in the
    /// installed PAC CLI. Resulting access-token carries <c>appid=9cee029c...</c> and is accepted
    /// by Pipeline-Backend (FOCI exchange to Power-Apps-Maker AppId no longer works as of 2026,
    /// so we authenticate as PAC directly).
    /// </summary>
    public const string PacCliClientId = "9cee029c-6210-4654-90bb-17e6e9d36617";

    private readonly ILogger<DataverseTokenProvider> _logger;
    private IPublicClientApplication? _app;
    private IPublicClientApplication? _makerApp;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _makerInitLock = new(1, 1);

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

    /// <summary>
    /// Acquire a token whose <c>appid</c> claim is the Power-Apps-Maker AppId. Required for the
    /// Pipeline-Backend mutating endpoints. **Silent only** — if no cached token exists, throws
    /// <see cref="PipelineAuthRequiredException"/> so the caller can route the user to the
    /// <c>pipeline_auth_init</c> tool to complete a Device Code Flow login.
    /// </summary>
    public async Task<string> GetMakerUiTokenAsync(string scope, CancellationToken ct = default)
    {
        // In-memory cache for this process lifetime.
        if (_makerAccessToken != null && DateTime.UtcNow < _makerAccessTokenExpiresUtc)
            return _makerAccessToken;

        // Persisted refresh token? Exchange it.
        var refreshToken = await LoadMakerRefreshTokenAsync();
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new PipelineAuthRequiredException(
                "No cached Power-Apps-Maker token found. Run pipeline_auth_init first to complete a one-time Device Code Flow login.");
        }

        var tenant = string.IsNullOrEmpty(_tenantId) ? "organizations" : _tenantId;
        var tokenUrl = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
        using var http = new HttpClient();
        var resp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = PacCliClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = scope
        }), CancellationToken.None);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // Refresh token may have expired — force the user to re-auth.
            try { File.Delete(MakerCachePath); } catch { }
            throw new PipelineAuthRequiredException(
                $"Cached Power-Apps-Maker refresh token rejected ({resp.StatusCode}). Run pipeline_auth_init to re-login. Server: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var te) ? te.GetInt32() : 3600;

        // Persist the rotated refresh token (Microsoft rotates it on every exchange).
        if (root.TryGetProperty("refresh_token", out var newRt) && newRt.GetString() is string rtStr && !string.IsNullOrEmpty(rtStr))
            await SaveMakerRefreshTokenAsync(rtStr);

        _makerAccessToken = accessToken;
        _makerAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        return accessToken;
    }

    /// <summary>
    /// Synchronous Device Code Flow to acquire a Maker-AppId token. Auto-opens the user's default
    /// browser with a pre-filled verification URL (no manual code-typing needed), then BLOCKS until
    /// the user finishes signing in (or hits the 15-min MSAL timeout). The resulting refresh-token
    /// lands in the persistent MSAL cache; subsequent <see cref="GetMakerUiTokenAsync"/> calls
    /// succeed silently — for weeks, until the refresh-token expires.
    /// </summary>
    /// <remarks>
    /// Synchronous (blocking) on purpose: a fire-and-forget background task does not survive the
    /// MCP-server restarts that Claude Code triggers between tool calls. Blocking inside one tool
    /// call keeps MSAL's polling loop in the same process, so the token reliably hits the cache.
    /// </remarks>
    /// <summary>
    /// Step 1 of the two-step Device-Code-Flow: request a code from the IdP, persist the
    /// <c>device_code</c> in a side-file, optionally open the user's browser. Returns immediately
    /// (no polling here) so the user can see the code and react.
    /// </summary>
    public async Task<(string VerificationUrl, string UserCode, string Message, bool BrowserOpened, int ExpiresIn)> RequestMakerDeviceCodeAsync(
        string scope)
    {
        var tenant = string.IsNullOrEmpty(_tenantId) ? "organizations" : _tenantId;
        var deviceCodeUrl = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode";

        using var http = new HttpClient();
        var dcResp = await http.PostAsync(deviceCodeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = PacCliClientId,
            ["scope"] = scope + " offline_access"
        }), CancellationToken.None);
        var dcBody = await dcResp.Content.ReadAsStringAsync();
        if (!dcResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Device code request failed: {dcResp.StatusCode} {dcBody}");

        using var dcDoc = JsonDocument.Parse(dcBody);
        var dcRoot = dcDoc.RootElement;
        var userCode = dcRoot.GetProperty("user_code").GetString()!;
        var deviceCode = dcRoot.GetProperty("device_code").GetString()!;
        var verificationUri = dcRoot.GetProperty("verification_uri").GetString()!;
        var message = dcRoot.GetProperty("message").GetString()!;
        var expiresIn = dcRoot.GetProperty("expires_in").GetInt32();
        var interval = dcRoot.TryGetProperty("interval", out var ivl) ? ivl.GetInt32() : 5;

        // Persist the pending device-code (separate small file). Picked up by Complete-step.
        var pending = new
        {
            device_code = deviceCode,
            scope,
            interval,
            expires_utc = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o")
        };
        await SavePendingDeviceCodeAsync(JsonSerializer.Serialize(pending));

        _logger.LogInformation("Device code: {Code} — URL: {Url}", userCode, verificationUri);

        // Best-effort browser auto-open.
        bool browserOpened = false;
        try
        {
            var openUrl = $"{verificationUri}?otc={Uri.EscapeDataString(userCode)}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = openUrl, UseShellExecute = true });
            browserOpened = true;
        }
        catch (Exception bex)
        {
            _logger.LogWarning(bex, "Could not auto-open browser");
        }

        return (verificationUri, userCode, message, browserOpened, expiresIn);
    }

    /// <summary>
    /// Step 2: poll the token endpoint using the device_code persisted by
    /// <see cref="RequestMakerDeviceCodeAsync"/>. The user must have completed the browser login first.
    /// Polling normally finishes in a few seconds after the user signs in.
    /// </summary>
    public async Task CompleteMakerDeviceCodeAsync(int pollSeconds = 60)
    {
        var pendingJson = await LoadPendingDeviceCodeAsync()
            ?? throw new InvalidOperationException("No pending device code. Call pipeline_auth_init first.");

        using var doc = JsonDocument.Parse(pendingJson);
        var deviceCode = doc.RootElement.GetProperty("device_code").GetString()!;
        var scope = doc.RootElement.GetProperty("scope").GetString()!;
        var interval = doc.RootElement.GetProperty("interval").GetInt32();
        var expiresUtc = DateTime.Parse(doc.RootElement.GetProperty("expires_utc").GetString()!).ToUniversalTime();

        if (DateTime.UtcNow > expiresUtc)
        {
            try { File.Delete(PendingDeviceCodePath); } catch { }
            throw new InvalidOperationException("Pending device code expired. Call pipeline_auth_init again.");
        }

        var tenant = string.IsNullOrEmpty(_tenantId) ? "organizations" : _tenantId;
        var tokenUrl = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
        using var http = new HttpClient();

        var deadline = DateTime.UtcNow.AddSeconds(Math.Min(pollSeconds, (expiresUtc - DateTime.UtcNow).TotalSeconds));
        while (DateTime.UtcNow < deadline)
        {
            var tokResp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = PacCliClientId,
                ["device_code"] = deviceCode
            }), CancellationToken.None);
            var tokBody = await tokResp.Content.ReadAsStringAsync();

            if (tokResp.IsSuccessStatusCode)
            {
                using var tokDoc = JsonDocument.Parse(tokBody);
                var tokRoot = tokDoc.RootElement;
                var accessToken = tokRoot.GetProperty("access_token").GetString()!;
                var refreshToken = tokRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                var tokenExpiresIn = tokRoot.TryGetProperty("expires_in", out var te) ? te.GetInt32() : 3600;

                if (refreshToken != null)
                {
                    await SaveMakerRefreshTokenAsync(refreshToken);
                    _logger.LogInformation("Power-Apps-Maker refresh token cached.");
                }
                _makerAccessToken = accessToken;
                _makerAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(tokenExpiresIn - 60);

                try { File.Delete(PendingDeviceCodePath); } catch { }
                return;
            }

            using var errDoc = JsonDocument.Parse(tokBody);
            var err = errDoc.RootElement.GetProperty("error").GetString();
            if (err == "authorization_pending")
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), CancellationToken.None);
                continue;
            }
            if (err == "slow_down")
            {
                interval += 5;
                await Task.Delay(TimeSpan.FromSeconds(interval), CancellationToken.None);
                continue;
            }
            try { File.Delete(PendingDeviceCodePath); } catch { }
            throw new InvalidOperationException($"Device code login: {err} — {tokBody}");
        }

        throw new InvalidOperationException(
            $"Polled {pollSeconds}s without authorization. User has not finished sign-in yet. Call pipeline_auth_complete again after signing in.");
    }

    private static string PendingDeviceCodePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dataverse-modelling-mcp");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pending-device-code.json");
        }
    }

    private static async Task SavePendingDeviceCodeAsync(string json) =>
        await File.WriteAllTextAsync(PendingDeviceCodePath, json);

    private static async Task<string?> LoadPendingDeviceCodeAsync() =>
        File.Exists(PendingDeviceCodePath) ? await File.ReadAllTextAsync(PendingDeviceCodePath) : null;

    private string? _makerAccessToken;
    private DateTime _makerAccessTokenExpiresUtc;

    private static string MakerCachePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dataverse-modelling-mcp");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "maker-refresh-token.bin");
        }
    }

    private static async Task SaveMakerRefreshTokenAsync(string refreshToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(refreshToken);
        if (OperatingSystem.IsWindows())
        {
            bytes = System.Security.Cryptography.ProtectedData.Protect(
                bytes, optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        await File.WriteAllBytesAsync(MakerCachePath, bytes);
    }

    private static async Task<string?> LoadMakerRefreshTokenAsync()
    {
        if (!File.Exists(MakerCachePath)) return null;
        var bytes = await File.ReadAllBytesAsync(MakerCachePath);
        if (OperatingSystem.IsWindows())
        {
            bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<IPublicClientApplication> GetOrCreateMakerAppAsync(CancellationToken ct)
    {
        if (_makerApp is not null)
            return _makerApp;

        await _makerInitLock.WaitAsync(ct);
        try
        {
            if (_makerApp is not null)
                return _makerApp;

            // Power-Apps-Maker is a Microsoft First-Party app — it does NOT accept the public
            // http://localhost redirect URI. We use the Device Code Flow, which has no redirect.
            var builder = PublicClientApplicationBuilder.Create(PowerAppsMakerClientId);

            if (!string.IsNullOrEmpty(_tenantId))
                builder = builder.WithTenantId(_tenantId);
            else
                builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs);

            var app = builder.Build();
            var cacheHelper = await SecureTokenCache.CreateAsync();
            cacheHelper.RegisterCache(app.UserTokenCache);

            _makerApp = app;
            return _makerApp;
        }
        finally
        {
            _makerInitLock.Release();
        }
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

/// <summary>
/// Thrown by <see cref="DataverseTokenProvider.GetMakerUiTokenAsync"/> when no cached Power-Apps-Maker
/// token is available. The tool layer should catch this and instruct the user to run pipeline_auth_init.
/// </summary>
public sealed class PipelineAuthRequiredException : InvalidOperationException
{
    public PipelineAuthRequiredException(string message) : base(message) { }
}

namespace Dataverse.Core.Auth;

public interface ITokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct = default);
}

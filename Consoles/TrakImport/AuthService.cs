using Microsoft.PowerPlatform.Dataverse.Client;

namespace TrakImport;

public class AuthService
{
	private readonly string _crmBaseUrl;

	public AuthService(AppConfig config)
	{
		_crmBaseUrl = config.CrmBaseUrl.TrimEnd('/');
	}

	public ServiceClient Connect()
	{
		Console.WriteLine("[Auth] Connecting to Dynamics 365...");
		Console.WriteLine("[Auth] A browser window will open for login (MFA supported).\n");

		var connectionString =
			$"AuthType=OAuth;" +
			$"Url={_crmBaseUrl};" +
			$"LoginPrompt=Always;" +
			$"RedirectUri=http://localhost;" +
			$"AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;" +
			$"RequireNewInstance=True";

		var client = new ServiceClient(connectionString);

		if (!client.IsReady)
			throw new Exception(
				$"ServiceClient connection failed: {client.LastError}\n{client.LastException?.Message}");

		Console.WriteLine($"[Auth] Connected: {client.ConnectedOrgFriendlyName}");
		Console.WriteLine($"[Auth] User: {client.OAuthUserId}\n");

		return client;
	}
}
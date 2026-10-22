using System;
using Windows.Security.Credentials;

namespace WanStudio.Services;

/// <summary>Persists DashScope API keys in Windows Credential Locker (per-user).</summary>
public static class ApiKeyStore
{
    private const string ResourceName = "WanStudio.DashScope";

    public static void Save(string profileName, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name required.", nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key required.", nameof(apiKey));
        }

        var vault = new PasswordVault();
        try
        {
            var existing = vault.Retrieve(ResourceName, profileName);
            vault.Remove(existing);
        }
        catch (Exception)
        {
            // no existing credential
        }

        vault.Add(new PasswordCredential(ResourceName, profileName, apiKey.Trim()));
    }

    public static string? TryGet(string profileName)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(ResourceName, profileName);
            cred.RetrievePassword();
            return cred.Password;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Delete(string profileName)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(ResourceName, profileName);
            vault.Remove(cred);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    public static string Mask(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return "(not saved)";
        }

        if (apiKey.Length <= 8)
        {
            return "****";
        }

        return apiKey[..4] + new string('*', Math.Min(12, apiKey.Length - 8)) + apiKey[^4..];
    }
}

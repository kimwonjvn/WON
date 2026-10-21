using System;
using System.Linq;

namespace Span.Services.Assistant;

/// <summary>
/// LLM API 키 보관소. Windows 자격 증명(PasswordVault)에 저장하며, 설정 파일/로그/저장소에는 남기지 않는다.
/// </summary>
public static class AssistantCredentialStore
{
    private const string Resource = "WON.Assistant";

    public static string? Load(string provider)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            var cred = vault.FindAllByResource(Resource).FirstOrDefault(c => c.UserName == provider);
            if (cred == null) return null;
            cred.RetrievePassword();
            return string.IsNullOrEmpty(cred.Password) ? null : cred.Password;
        }
        catch
        {
            return null; // 항목 없음
        }
    }

    public static bool Save(string provider, string apiKey)
    {
        try
        {
            Delete(provider);
            var vault = new Windows.Security.Credentials.PasswordVault();
            vault.Add(new Windows.Security.Credentials.PasswordCredential(Resource, provider, apiKey));
            return true;
        }
        catch (Exception ex)
        {
            Helpers.DebugLogger.Log($"[Assistant] key save failed: {ex.GetType().Name}");
            return false;
        }
    }

    public static void Delete(string provider)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            foreach (var c in vault.FindAllByResource(Resource).Where(c => c.UserName == provider).ToList())
                vault.Remove(c);
        }
        catch { /* 항목 없음 */ }
    }
}

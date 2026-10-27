using System.Security.Cryptography;
using System.Text;

namespace Project_no._1.Services;

public static class ProjectWorkspace
{
    public const string CookieName = "titanai-workspace";

    public static string? GetOwnerId(HttpContext context)
    {
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
        {
            return null;
        }

        try
        {
            return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static void IssueCookie(HttpContext context)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });
    }
}

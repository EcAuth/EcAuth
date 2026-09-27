using System.Net.Http.Headers;
using IdentityProvider.Models;
using IdentityProvider.Services;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// マイページ API 共通の Bearer 検証。<c>Authorization: Bearer</c> を <see cref="ITokenService"/> で検証し、
    /// <see cref="SubjectType.Account"/> のトークンだけを受理する（<see cref="AccountController"/> と
    /// <see cref="BillingController"/> が共用）。ASP.NET Core の認証ミドルウェアは使っていないため、
    /// Account 向けエンドポイントは各アクションの先頭でこれを呼ぶ。
    /// </summary>
    public static class AccountTokenAuthentication
    {
        /// <summary>有効な Account トークンなら subject、そうでなければ null。</summary>
        public static async Task<string?> ValidateAccountTokenAsync(HttpRequest request, ITokenService tokenService)
        {
            var authorizationHeader = request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                return null;
            }

            AuthenticationHeaderValue authHeaderValue;
            try
            {
                authHeaderValue = AuthenticationHeaderValue.Parse(authorizationHeader);
            }
            catch (FormatException)
            {
                return null;
            }

            // RFC 7235: auth-scheme は大文字小文字を区別しない
            if (!string.Equals(authHeaderValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(authHeaderValue.Parameter))
            {
                return null;
            }

            var validation = await tokenService.ValidateAccessTokenWithTypeAsync(authHeaderValue.Parameter);
            if (!validation.IsValid || validation.SubjectType != SubjectType.Account || string.IsNullOrEmpty(validation.Subject))
            {
                return null;
            }

            return validation.Subject;
        }

        /// <summary>401 のレスポンスボディ（<see cref="AccountController"/> と同じ文言）。</summary>
        public static object InvalidTokenBody() => new
        {
            error = "invalid_token",
            error_description = "有効な Account アクセストークンが必要です。"
        };
    }
}

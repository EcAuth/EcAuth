using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace IdentityProvider.Security
{
    /// <summary>
    /// PKCE (RFC 7636) の code_verifier 検証ユーティリティ。
    /// 本 IdP は S256 変換のみサポートする（plain は許容しない）。
    /// </summary>
    public static class PkceValidator
    {
        /// <summary>
        /// PKCE (RFC 7636 Section 4.2) の code_challenge = 43*128unreserved。
        /// unreserved = ALPHA / DIGIT / "-" / "." / "_" / "~"
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex CodeChallengePattern =
            new(@"^[A-Za-z0-9\-._~]{43,128}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// code_challenge が RFC 7636 Section 4.2 の形式（43〜128 文字の unreserved）かを検証する。
        /// 認可コードへ束縛する前に呼ぶこと。形式不正を素通しすると、トークン交換時まで
        /// 誤りが発覚せず（S256 変換ミス等の原因究明が困難になる）、128 文字超は
        /// AuthorizationCode.CodeChallenge の MaxLength(128) に阻まれ 500 になる。
        /// </summary>
        public static bool IsValidChallengeFormat(string? codeChallenge)
        {
            return !string.IsNullOrEmpty(codeChallenge) && CodeChallengePattern.IsMatch(codeChallenge);
        }

        /// <summary>
        /// code_challenge_method が本 IdP のサポート範囲（未指定 = S256 既定、または "S256"）かを検証する。
        /// </summary>
        public static bool IsSupportedMethod(string? method)
        {
            return string.IsNullOrEmpty(method) || method == "S256";
        }

        /// <summary>
        /// code_verifier が code_challenge と一致するか検証する。
        /// method は "S256" のみ許容し、それ以外は false を返す。
        /// </summary>
        /// <param name="codeVerifier">クライアントが提示した code_verifier</param>
        /// <param name="codeChallenge">認可コードに束縛された code_challenge</param>
        /// <param name="method">code_challenge_method（S256 のみ）</param>
        public static bool Verify(string? codeVerifier, string? codeChallenge, string? method)
        {
            if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
                return false;

            // RFC 7636 Section 4.1: code_verifier は 43〜128 文字。
            // 範囲外は不正として即座に false（極端に長い入力による SHA256 の資源枯渇も防ぐ）。
            if (codeVerifier.Length < 43 || codeVerifier.Length > 128)
                return false;

            // 未指定時は S256 を既定とする（本 IdP は S256 のみ）
            var m = string.IsNullOrEmpty(method) ? "S256" : method;
            if (m != "S256")
                return false;

            // RFC 7636: BASE64URL-ENCODE(SHA256(ASCII(code_verifier)))
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            var computed = Base64UrlTextEncoder.Encode(hash);

            // 定数時間比較（両者を UTF-8 バイト列にして比較）
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(codeChallenge));
        }
    }
}

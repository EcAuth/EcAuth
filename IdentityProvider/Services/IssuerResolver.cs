namespace IdentityProvider.Services
{
    /// <summary>
    /// issuer 値（iss クレーム / Discovery の issuer）の単一ソース。
    /// 案A サブドメイン型に基づき、設定値ではなく HttpContext から動的に取得する。
    /// </summary>
    public interface IIssuerResolver
    {
        /// <summary>
        /// 現在のリクエストから issuer 値を「{Scheme}://{Host}」形式で返す。
        /// HttpContext が利用できない場合は InvalidOperationException を投げる。
        /// </summary>
        string GetIssuer();
    }

    public class IssuerResolver : IIssuerResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public IssuerResolver(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string GetIssuer()
        {
            var request = _httpContextAccessor.HttpContext?.Request;
            if (request != null)
            {
                return $"{request.Scheme}://{request.Host}";
            }

            throw new InvalidOperationException("HttpContext is not available. Cannot determine issuer.");
        }
    }
}

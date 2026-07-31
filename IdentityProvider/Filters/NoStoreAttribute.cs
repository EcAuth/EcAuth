using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace IdentityProvider.Filters
{
    /// <summary>
    /// トークン・認可コード・client_secret 等のクレデンシャルを含むレスポンスに
    /// <c>Cache-Control: no-store</c> / <c>Pragma: no-cache</c> を付与する。
    ///
    /// RFC 6749 §5.1（token endpoint の成功レスポンスは no-store 必須）および
    /// RFC 6750 §5.3 に基づく。ブラウザ・中間プロキシ・CDN がレスポンスボディを
    /// ディスクへ保存すると、共用端末の履歴やキャッシュからクレデンシャルが
    /// 復元され得るため、クレデンシャルを返す全エンドポイントに適用する。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public sealed class NoStoreAttribute : ActionFilterAttribute
    {
        public override void OnResultExecuting(ResultExecutingContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers[HeaderNames.CacheControl] = "no-store";
            headers[HeaderNames.Pragma] = "no-cache";
            base.OnResultExecuting(context);
        }
    }
}

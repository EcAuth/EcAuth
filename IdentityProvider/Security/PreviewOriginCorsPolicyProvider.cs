using IdentityProvider.Controllers;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Security
{
    /// <summary>
    /// <see cref="SignupController.CorsPolicy"/> に、リクエストのテナントで許可された PR プレビューのオリジン
    /// （<see cref="IPreviewOriginResolver"/>）を足す（EcAuthDocs#159）。
    /// <para>
    /// 固定オリジン（<c>Signup:AllowedOrigins</c>）は accounts / stg-accounts で共通だが、プレビューは
    /// stg-accounts だけに許したい。CORS ポリシーの <c>IsOriginAllowed</c> はオリジンしか受け取らないため、
    /// テナント（Host から <c>TenantMiddleware</c> が解決）を見られるよう、ポリシーをリクエストごとに組み直す。
    /// そのため <c>app.UseCors()</c> は <c>TenantMiddleware</c> の後に置く（Program.cs）。
    /// </para>
    /// <para>
    /// テナントにパターンが無ければ既定のポリシーをそのまま返す（accounts の挙動は変わらない）。
    /// </para>
    /// </summary>
    public sealed class PreviewOriginCorsPolicyProvider : ICorsPolicyProvider
    {
        private readonly DefaultCorsPolicyProvider _inner;

        public PreviewOriginCorsPolicyProvider(IOptions<CorsOptions> options)
        {
            _inner = new DefaultCorsPolicyProvider(options);
        }

        public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
        {
            var policy = await _inner.GetPolicyAsync(context, policyName);
            if (policy == null || policyName != SignupController.CorsPolicy)
            {
                return policy;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (policy.IsOriginAllowed(origin))
            {
                return policy;
            }

            var tenantName = context.RequestServices.GetRequiredService<ITenantService>().TenantName;
            var previewOrigins = context.RequestServices.GetRequiredService<IPreviewOriginResolver>();
            if (!previewOrigins.IsAllowed(tenantName, origin))
            {
                return policy;
            }

            // このリクエストのオリジンも許す判定に差し替えたポリシー。既定の判定（Origins との完全一致）ではなくなるので、
            // CorsService が Vary: Origin を付ける（応答が Origin ごとに変わるため必要）。
            return new CorsPolicyBuilder(policy)
                .SetIsOriginAllowed(o => policy.IsOriginAllowed(o) || string.Equals(o, origin, StringComparison.Ordinal))
                .Build();
        }
    }
}

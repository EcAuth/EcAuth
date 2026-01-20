using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class AuthorizationCodeTests
    {
        [Fact]
        public void AuthorizationCode_DefaultValues_ShouldBeSetCorrectly()
        {
            var authCode = new AuthorizationCode();

            Assert.Equal(string.Empty, authCode.Code);
            Assert.Null(authCode.EcAuthSubject);  // nullable化された
            Assert.Null(authCode.B2BSubject);     // B2B用のSubject
            Assert.Equal(0, authCode.ClientId);
            Assert.Equal(string.Empty, authCode.RedirectUri);
            Assert.Null(authCode.Scope);
            Assert.Null(authCode.State);
            Assert.Equal(DateTimeOffset.MinValue, authCode.ExpiresAt);
            Assert.False(authCode.IsUsed);
            Assert.True(authCode.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.Null(authCode.UsedAt);
            Assert.Null(authCode.EcAuthUser);
            Assert.Null(authCode.B2BUser);        // B2B用のナビゲーションプロパティ
            Assert.Null(authCode.Client);
        }

        [Fact]
        public void AuthorizationCode_SetProperties_ShouldRetainValues()
        {
            var code = "test-auth-code-123";
            var subject = "test-subject";
            var clientId = 1;
            var redirectUri = "https://example.com/callback";
            var scope = "openid profile";
            var state = "test-state";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            var isUsed = true;
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var usedAt = DateTimeOffset.UtcNow;

            var authCode = new AuthorizationCode
            {
                Code = code,
                EcAuthSubject = subject,
                ClientId = clientId,
                RedirectUri = redirectUri,
                Scope = scope,
                State = state,
                ExpiresAt = expiresAt,
                IsUsed = isUsed,
                CreatedAt = createdAt,
                UsedAt = usedAt
            };

            Assert.Equal(code, authCode.Code);
            Assert.Equal(subject, authCode.EcAuthSubject);
            Assert.Equal(clientId, authCode.ClientId);
            Assert.Equal(redirectUri, authCode.RedirectUri);
            Assert.Equal(scope, authCode.Scope);
            Assert.Equal(state, authCode.State);
            Assert.Equal(expiresAt, authCode.ExpiresAt);
            Assert.Equal(isUsed, authCode.IsUsed);
            Assert.Equal(createdAt, authCode.CreatedAt);
            Assert.Equal(usedAt, authCode.UsedAt);
        }

        [Theory]
        [InlineData("")]
        [InlineData("auth-code-123")]
        [InlineData("very-long-authorization-code-12345")]
        public void AuthorizationCode_Code_ShouldAcceptValidValues(string code)
        {
            var authCode = new AuthorizationCode { Code = code };
            
            Assert.Equal(code, authCode.Code);
        }

        [Theory]
        [InlineData("https://example.com/callback")]
        [InlineData("https://localhost:3000/auth/callback")]
        [InlineData("http://dev.example.com/oauth/callback")]
        public void AuthorizationCode_RedirectUri_ShouldAcceptValidUris(string redirectUri)
        {
            var authCode = new AuthorizationCode { RedirectUri = redirectUri };
            
            Assert.Equal(redirectUri, authCode.RedirectUri);
        }

        [Theory]
        [InlineData("openid")]
        [InlineData("openid profile")]
        [InlineData("openid profile email")]
        [InlineData(null)]
        public void AuthorizationCode_Scope_ShouldAcceptValidValues(string? scope)
        {
            var authCode = new AuthorizationCode { Scope = scope };
            
            Assert.Equal(scope, authCode.Scope);
        }

        [Fact]
        public void AuthorizationCode_IsExpired_ShouldWorkCorrectly()
        {
            var expiredCode = new AuthorizationCode 
            { 
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) 
            };
            
            var validCode = new AuthorizationCode 
            { 
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) 
            };

            Assert.True(expiredCode.ExpiresAt < DateTimeOffset.UtcNow);
            Assert.True(validCode.ExpiresAt > DateTimeOffset.UtcNow);
        }

        [Fact]
        public void AuthorizationCode_UsageTracking_ShouldWork()
        {
            var authCode = new AuthorizationCode();
            
            Assert.False(authCode.IsUsed);
            Assert.Null(authCode.UsedAt);
            
            authCode.IsUsed = true;
            authCode.UsedAt = DateTimeOffset.UtcNow;
            
            Assert.True(authCode.IsUsed);
            Assert.NotNull(authCode.UsedAt);
            Assert.True(authCode.UsedAt <= DateTimeOffset.UtcNow);
        }

        [Fact]
        public void AuthorizationCode_Relations_ShouldWork()
        {
            var user = new EcAuthUser { Subject = "test-subject" };
            var client = new Client { Id = 1, ClientId = "test-client" };
            var authCode = new AuthorizationCode 
            { 
                EcAuthUser = user,
                Client = client,
                ClientId = 1
            };
            
            Assert.Equal(user, authCode.EcAuthUser);
            Assert.Equal(client, authCode.Client);
        }
    }
}
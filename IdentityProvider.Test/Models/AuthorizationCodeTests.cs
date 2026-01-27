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
            Assert.Equal(string.Empty, authCode.Subject);
            Assert.Equal(default(SubjectType), authCode.SubjectType);
            Assert.Equal(0, authCode.ClientId);
            Assert.Equal(string.Empty, authCode.RedirectUri);
            Assert.Null(authCode.Scope);
            Assert.Null(authCode.State);
            Assert.Equal(DateTimeOffset.MinValue, authCode.ExpiresAt);
            Assert.False(authCode.IsUsed);
            Assert.True(authCode.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.Null(authCode.UsedAt);
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
                Subject = subject,
                SubjectType = SubjectType.B2C,
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
            Assert.Equal(subject, authCode.Subject);
            Assert.Equal(SubjectType.B2C, authCode.SubjectType);
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
            var client = new Client { Id = 1, ClientId = "test-client" };
            var authCode = new AuthorizationCode
            {
                Subject = "test-subject",
                SubjectType = SubjectType.B2C,
                Client = client,
                ClientId = 1
            };

            Assert.Equal(client, authCode.Client);
        }

        [Theory]
        [InlineData(SubjectType.B2C)]
        [InlineData(SubjectType.B2B)]
        [InlineData(SubjectType.Account)]
        public void AuthorizationCode_SubjectType_ShouldAcceptValidValues(SubjectType subjectType)
        {
            var authCode = new AuthorizationCode { SubjectType = subjectType };

            Assert.Equal(subjectType, authCode.SubjectType);
        }
    }
}

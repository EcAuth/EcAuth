using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class WebAuthnChallengeTests
    {
        [Fact]
        public void WebAuthnChallenge_DefaultValues_ShouldBeSetCorrectly()
        {
            var challenge = new WebAuthnChallenge();

            Assert.Equal(0, challenge.Id);
            Assert.Equal(string.Empty, challenge.Challenge);
            Assert.Equal(string.Empty, challenge.SessionId);
            Assert.Equal(string.Empty, challenge.Type);
            Assert.Equal(string.Empty, challenge.UserType);
            Assert.Null(challenge.Subject);
            Assert.Null(challenge.RpId);
            Assert.Equal(0, challenge.ClientId);
            Assert.True(challenge.ExpiresAt <= DateTimeOffset.UtcNow);
            Assert.True(challenge.CreatedAt <= DateTimeOffset.UtcNow);
        }

        [Fact]
        public void WebAuthnChallenge_SetProperties_ShouldRetainValues()
        {
            var id = 123;
            var challengeValue = "base64url-encoded-challenge";
            var sessionId = "random-session-id";
            var type = "registration";
            var userType = "b2b";
            var subject = "test-subject-uuid";
            var rpId = "shop.example.com";
            var clientId = 1;
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            var createdAt = DateTimeOffset.UtcNow;

            var challenge = new WebAuthnChallenge
            {
                Id = id,
                Challenge = challengeValue,
                SessionId = sessionId,
                Type = type,
                UserType = userType,
                Subject = subject,
                RpId = rpId,
                ClientId = clientId,
                ExpiresAt = expiresAt,
                CreatedAt = createdAt
            };

            Assert.Equal(id, challenge.Id);
            Assert.Equal(challengeValue, challenge.Challenge);
            Assert.Equal(sessionId, challenge.SessionId);
            Assert.Equal(type, challenge.Type);
            Assert.Equal(userType, challenge.UserType);
            Assert.Equal(subject, challenge.Subject);
            Assert.Equal(rpId, challenge.RpId);
            Assert.Equal(clientId, challenge.ClientId);
            Assert.Equal(expiresAt, challenge.ExpiresAt);
            Assert.Equal(createdAt, challenge.CreatedAt);
        }

        [Theory]
        [InlineData("registration")]
        [InlineData("authentication")]
        public void WebAuthnChallenge_Type_ShouldAcceptValidValues(string type)
        {
            var challenge = new WebAuthnChallenge { Type = type };

            Assert.Equal(type, challenge.Type);
        }

        [Theory]
        [InlineData("b2b")]
        [InlineData("b2c")]
        public void WebAuthnChallenge_UserType_ShouldAcceptValidValues(string userType)
        {
            var challenge = new WebAuthnChallenge { UserType = userType };

            Assert.Equal(userType, challenge.UserType);
        }

        [Fact]
        public void WebAuthnChallenge_IsExpired_ShouldReturnTrueForExpiredChallenge()
        {
            var challenge = new WebAuthnChallenge
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };

            Assert.True(challenge.IsExpired);
        }

        [Fact]
        public void WebAuthnChallenge_IsExpired_ShouldReturnFalseForValidChallenge()
        {
            var challenge = new WebAuthnChallenge
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            Assert.False(challenge.IsExpired);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("test-subject-uuid")]
        public void WebAuthnChallenge_Subject_ShouldAcceptValidValues(string? subject)
        {
            var challenge = new WebAuthnChallenge { Subject = subject };

            Assert.Equal(subject, challenge.Subject);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("shop.example.com")]
        [InlineData("admin.example.com")]
        public void WebAuthnChallenge_RpId_ShouldAcceptValidValues(string? rpId)
        {
            var challenge = new WebAuthnChallenge { RpId = rpId };

            Assert.Equal(rpId, challenge.RpId);
        }

        [Fact]
        public void WebAuthnChallenge_ClientRelation_ShouldWork()
        {
            var client = new Client
            {
                Id = 1,
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AppName = "Test App"
            };

            var challenge = new WebAuthnChallenge
            {
                ClientId = client.Id,
                Client = client
            };

            Assert.NotNull(challenge.Client);
            Assert.Equal(client.Id, challenge.ClientId);
            Assert.Equal(client, challenge.Client);
        }
    }
}

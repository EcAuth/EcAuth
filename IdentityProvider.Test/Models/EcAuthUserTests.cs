using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class EcAuthUserTests
    {
        [Fact]
        public void EcAuthUser_DefaultValues_ShouldBeSetCorrectly()
        {
            var user = new EcAuthUser();

            Assert.Equal(0, user.Id);
            Assert.Equal(string.Empty, user.Subject);
            Assert.Equal(string.Empty, user.EmailHash);
            Assert.Equal(0, user.OrganizationId);
            Assert.True(user.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.True(user.UpdatedAt <= DateTimeOffset.UtcNow);
            Assert.NotNull(user.ExternalIdpMappings);
        }

        [Fact]
        public void EcAuthUser_SetProperties_ShouldRetainValues()
        {
            var id = 123;
            var subject = "test-subject-uuid";
            var emailHash = "testhash123";
            var organizationId = 1;
            var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
            var updatedAt = DateTimeOffset.UtcNow;

            var user = new EcAuthUser
            {
                Id = id,
                Subject = subject,
                EmailHash = emailHash,
                OrganizationId = organizationId,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            };

            Assert.Equal(id, user.Id);
            Assert.Equal(subject, user.Subject);
            Assert.Equal(emailHash, user.EmailHash);
            Assert.Equal(organizationId, user.OrganizationId);
            Assert.Equal(createdAt, user.CreatedAt);
            Assert.Equal(updatedAt, user.UpdatedAt);
        }

        [Fact]
        public void EcAuthUser_Collections_ShouldBeEmptyByDefault()
        {
            var user = new EcAuthUser();

            Assert.Empty(user.ExternalIdpMappings);
        }

        [Theory]
        [InlineData("")]
        [InlineData("valid-subject")]
        [InlineData("another-test-subject-12345")]
        public void EcAuthUser_Subject_ShouldAcceptValidValues(string subject)
        {
            var user = new EcAuthUser { Subject = subject };
            
            Assert.Equal(subject, user.Subject);
        }

        [Theory]
        [InlineData("")]
        [InlineData("ABCD1234")]
        [InlineData("sha256hash")]
        public void EcAuthUser_EmailHash_ShouldAcceptValidValues(string emailHash)
        {
            var user = new EcAuthUser { EmailHash = emailHash };
            
            Assert.Equal(emailHash, user.EmailHash);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(999999)]
        public void EcAuthUser_OrganizationId_ShouldAcceptValidValues(int organizationId)
        {
            var user = new EcAuthUser { OrganizationId = organizationId };

            Assert.Equal(organizationId, user.OrganizationId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(999999)]
        public void EcAuthUser_Id_ShouldAcceptValidValues(int id)
        {
            var user = new EcAuthUser { Id = id };

            Assert.Equal(id, user.Id);
        }
    }
}
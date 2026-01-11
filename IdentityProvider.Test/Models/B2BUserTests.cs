using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class B2BUserTests
    {
        [Fact]
        public void B2BUser_DefaultValues_ShouldBeSetCorrectly()
        {
            var user = new B2BUser();

            Assert.Equal(0, user.Id);
            Assert.Equal(string.Empty, user.Subject);
            Assert.Null(user.ExternalId);
            Assert.Equal("admin", user.UserType);
            Assert.Equal(0, user.OrganizationId);
            Assert.True(user.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.True(user.UpdatedAt <= DateTimeOffset.UtcNow);
            Assert.NotNull(user.PasskeyCredentials);
        }

        [Fact]
        public void B2BUser_SetProperties_ShouldRetainValues()
        {
            var id = 123;
            var subject = "test-subject-uuid";
            var externalId = "admin001";
            var userType = "staff";
            var organizationId = 1;
            var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
            var updatedAt = DateTimeOffset.UtcNow;

            var user = new B2BUser
            {
                Id = id,
                Subject = subject,
                ExternalId = externalId,
                UserType = userType,
                OrganizationId = organizationId,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            };

            Assert.Equal(id, user.Id);
            Assert.Equal(subject, user.Subject);
            Assert.Equal(externalId, user.ExternalId);
            Assert.Equal(userType, user.UserType);
            Assert.Equal(organizationId, user.OrganizationId);
            Assert.Equal(createdAt, user.CreatedAt);
            Assert.Equal(updatedAt, user.UpdatedAt);
        }

        [Fact]
        public void B2BUser_PasskeyCredentials_ShouldBeEmptyByDefault()
        {
            var user = new B2BUser();

            Assert.Empty(user.PasskeyCredentials);
        }

        [Theory]
        [InlineData("")]
        [InlineData("valid-subject")]
        [InlineData("another-test-subject-12345")]
        public void B2BUser_Subject_ShouldAcceptValidValues(string subject)
        {
            var user = new B2BUser { Subject = subject };

            Assert.Equal(subject, user.Subject);
        }

        [Theory]
        [InlineData("admin")]
        [InlineData("staff")]
        [InlineData("manager")]
        public void B2BUser_UserType_ShouldAcceptValidValues(string userType)
        {
            var user = new B2BUser { UserType = userType };

            Assert.Equal(userType, user.UserType);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("login_id_001")]
        [InlineData("admin@shop.example.com")]
        public void B2BUser_ExternalId_ShouldAcceptValidValues(string? externalId)
        {
            var user = new B2BUser { ExternalId = externalId };

            Assert.Equal(externalId, user.ExternalId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(999999)]
        public void B2BUser_OrganizationId_ShouldAcceptValidValues(int organizationId)
        {
            var user = new B2BUser { OrganizationId = organizationId };

            Assert.Equal(organizationId, user.OrganizationId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(999999)]
        public void B2BUser_Id_ShouldAcceptValidValues(int id)
        {
            var user = new B2BUser { Id = id };

            Assert.Equal(id, user.Id);
        }
    }
}

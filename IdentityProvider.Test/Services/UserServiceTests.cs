using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="UserService"/> の参照系。外部 IdP ログインの解決（JIT プロビジョニング・統合）は
    /// <see cref="UserServiceFederatedLoginTests"/> と <see cref="UserServiceRelationalTests"/>。
    /// </summary>
    public class UserServiceTests
    {
        [Fact]
        public async Task GetUserBySubjectAsync_ExistingUser_ShouldReturnUser()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };
            context.EcAuthUsers.Add(user);
            await context.SaveChangesAsync();

            // Act
            var result = await service.GetUserBySubjectAsync("test-subject");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(user.Subject, result.Subject);
            Assert.Equal(user.EmailHash, result.EmailHash);
            Assert.Equal(user.OrganizationId, result.OrganizationId);
        }

        [Fact]
        public async Task GetUserBySubjectAsync_NonExistingUser_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Act
            var result = await service.GetUserBySubjectAsync("non-existing-subject");

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public async Task GetUserBySubjectAsync_InvalidSubject_ShouldReturnNull(string? subject)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Act
            var result = await service.GetUserBySubjectAsync(subject);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetUsersByEmailHashAsync_ExistingUsers_ShouldReturnUsers()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization1 = new Organization { Id = 1, Code = "ORG1", Name = "Org1", TenantName = "test-tenant" };
            var organization2 = new Organization { Id = 2, Code = "ORG2", Name = "Org2", TenantName = "test-tenant" };
            context.Organizations.AddRange(organization1, organization2);

            var emailHash = EmailHashUtil.HashEmail("test@example.com");
            var user1 = new EcAuthUser
            {
                Subject = "user1",
                EmailHash = emailHash,
                OrganizationId = 1
            };
            var user2 = new EcAuthUser
            {
                Subject = "user2",
                EmailHash = emailHash,
                OrganizationId = 2
            };
            context.EcAuthUsers.AddRange(user1, user2);
            await context.SaveChangesAsync();

            // Act - 全組織検索
            var allUsers = await service.GetUsersByEmailHashAsync(emailHash);

            // Assert
            Assert.Equal(2, allUsers.Count);
            Assert.Contains(allUsers, u => u.Subject == "user1");
            Assert.Contains(allUsers, u => u.Subject == "user2");
        }

        [Fact]
        public async Task GetUsersByEmailHashAsync_SpecificOrganization_ShouldReturnFilteredUsers()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization1 = new Organization { Id = 1, Code = "ORG1", Name = "Org1", TenantName = "test-tenant" };
            var organization2 = new Organization { Id = 2, Code = "ORG2", Name = "Org2", TenantName = "test-tenant" };
            context.Organizations.AddRange(organization1, organization2);

            var emailHash = EmailHashUtil.HashEmail("test@example.com");
            var user1 = new EcAuthUser
            {
                Subject = "user1",
                EmailHash = emailHash,
                OrganizationId = 1
            };
            var user2 = new EcAuthUser
            {
                Subject = "user2",
                EmailHash = emailHash,
                OrganizationId = 2
            };
            context.EcAuthUsers.AddRange(user1, user2);
            await context.SaveChangesAsync();

            // Act - 組織1のみ検索
            var org1Users = await service.GetUsersByEmailHashAsync(emailHash, 1);

            // Assert
            Assert.Single(org1Users);
            Assert.Equal("user1", org1Users[0].Subject);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public async Task GetUsersByEmailHashAsync_InvalidEmailHash_ShouldReturnEmptyList(string? emailHash)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Act
            var result = await service.GetUsersByEmailHashAsync(emailHash);

            // Assert
            Assert.Empty(result);
        }
    }
}

using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    public class UserServiceTests
    {
        [Fact]
        public async Task GetOrCreateUserAsync_NewUser_ShouldCreateUserAndMapping()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();

            var request = new IUserService.UserCreationRequest
            {
                ExternalProvider = "google",
                ExternalSubject = "google-user-123",
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };

            // Act
            var result = await service.GetOrCreateUserAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.Subject);
            Assert.Equal(request.EmailHash, result.EmailHash);
            Assert.Equal(request.OrganizationId, result.OrganizationId);

            // Verify user was created in database
            var userInDb = await context.EcAuthUsers.FirstOrDefaultAsync(u => u.Subject == result.Subject);
            Assert.NotNull(userInDb);

            // Verify external IdP mapping was created
            var mappingInDb = context.ExternalIdpMappings
                .FirstOrDefault(m => m.EcAuthSubject == result.Subject);
            Assert.NotNull(mappingInDb);
            Assert.Equal(request.ExternalProvider, mappingInDb.ExternalProvider);
            Assert.Equal(request.ExternalSubject, mappingInDb.ExternalSubject);
        }

        [Fact]
        public async Task GetOrCreateUserAsync_ExistingUser_ShouldReturnExistingUser()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var existingUser = new EcAuthUser
            {
                Subject = Guid.NewGuid().ToString(),
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };
            context.EcAuthUsers.Add(existingUser);

            var existingMapping = new ExternalIdpMapping
            {
                EcAuthSubject = existingUser.Subject,
                ExternalProvider = "google",
                ExternalSubject = "google-user-123"
            };
            context.ExternalIdpMappings.Add(existingMapping);
            await context.SaveChangesAsync();

            var request = new IUserService.UserCreationRequest
            {
                ExternalProvider = "google",
                ExternalSubject = "google-user-123",
                EmailHash = EmailHashUtil.HashEmail("updated@example.com"),
                OrganizationId = 1
            };

            // Act
            var result = await service.GetOrCreateUserAsync(request);

            // Assert
            Assert.Equal(existingUser.Subject, result.Subject);
            Assert.Equal(request.EmailHash, result.EmailHash); // Should be updated
            Assert.Equal(existingUser.OrganizationId, result.OrganizationId);

            // Verify email hash was updated
            var userInDb = await context.EcAuthUsers.FirstOrDefaultAsync(u => u.Subject == existingUser.Subject);
            Assert.NotNull(userInDb);
            Assert.Equal(request.EmailHash, userInDb.EmailHash);
        }

        [Theory]
        [InlineData(null, "external-subject", "email-hash", 1)]
        [InlineData("", "external-subject", "email-hash", 1)]
        [InlineData("google", null, "email-hash", 1)]
        [InlineData("google", "", "email-hash", 1)]
        [InlineData("google", "external-subject", "email-hash", 0)]
        [InlineData("google", "external-subject", "email-hash", -1)]
        public async Task GetOrCreateUserAsync_InvalidRequest_ShouldThrowArgumentException(
            string? externalProvider, string? externalSubject, string? emailHash, int organizationId)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            var request = new IUserService.UserCreationRequest
            {
                ExternalProvider = externalProvider ?? "",
                ExternalSubject = externalSubject ?? "",
                EmailHash = emailHash ?? "",
                OrganizationId = organizationId
            };

            await Assert.ThrowsAsync<ArgumentException>(() => service.GetOrCreateUserAsync(request));
        }

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
        public async Task GetUserByExternalIdAsync_ExistingMapping_ShouldReturnUser()
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

            var mapping = new ExternalIdpMapping
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google",
                ExternalSubject = "google-user-123"
            };
            context.ExternalIdpMappings.Add(mapping);
            await context.SaveChangesAsync();

            // Act
            var result = await service.GetUserByExternalIdAsync("google", "google-user-123", 1);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(user.Subject, result.Subject);
            Assert.Equal(user.EmailHash, result.EmailHash);
            Assert.Equal(user.OrganizationId, result.OrganizationId);
        }

        [Fact]
        public async Task GetUserByExternalIdAsync_DifferentOrganization_ShouldReturnNull()
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

            var mapping = new ExternalIdpMapping
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google",
                ExternalSubject = "google-user-123"
            };
            context.ExternalIdpMappings.Add(mapping);
            await context.SaveChangesAsync();

            // Act - Search for different organization
            var result = await service.GetUserByExternalIdAsync("google", "google-user-123", 2);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetUserByExternalIdAsync_NonExistingMapping_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Act
            var result = await service.GetUserByExternalIdAsync("google", "non-existing-subject", 1);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null, "external-subject")]
        [InlineData("", "external-subject")]
        [InlineData("google", null)]
        [InlineData("google", "")]
        public async Task GetUserByExternalIdAsync_InvalidParameters_ShouldReturnNull(
            string? externalProvider, string? externalSubject)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Act
            var result = await service.GetUserByExternalIdAsync(externalProvider, externalSubject, 1);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task CreateOrUpdateFromExternalAsync_NewUser_ShouldCreateUser()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();

            var externalUser = new ExternalUserInfo
            {
                Subject = "google-user-123",
                Email = "test@example.com",
                Name = "Test User",
                Provider = "google",
                Claims = new Dictionary<string, object> { { "email_verified", "true" } }
            };

            // Act
            var result = await service.CreateOrUpdateFromExternalAsync(externalUser, 1);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.Subject);
            Assert.Equal(EmailHashUtil.HashEmail("test@example.com"), result.EmailHash);
            Assert.Equal(1, result.OrganizationId);

            // Verify external mapping was created
            var mappingInDb = context.ExternalIdpMappings
                .FirstOrDefault(m => m.EcAuthSubject == result.Subject);
            Assert.NotNull(mappingInDb);
            Assert.Equal("google", mappingInDb.ExternalProvider);
            Assert.Equal("google-user-123", mappingInDb.ExternalSubject);
        }

        [Fact]
        public async Task CreateOrUpdateFromExternalAsync_UserWithoutEmail_ShouldCreateUserWithEmptyEmailHash()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            // Arrange
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();

            var externalUser = new ExternalUserInfo
            {
                Subject = "line-user-456",
                Email = null, // LINEユーザーなどメールが提供されない場合
                Name = "LINE User",
                Provider = "line"
            };

            // Act
            var result = await service.CreateOrUpdateFromExternalAsync(externalUser, 1);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(string.Empty, result.EmailHash);
            Assert.Equal(1, result.OrganizationId);
        }

        [Theory]
        [InlineData(null, "google", 1)]
        [InlineData("", "google", 1)]
        [InlineData("subject", null, 1)]
        [InlineData("subject", "", 1)]
        [InlineData("subject", "google", 0)]
        [InlineData("subject", "google", -1)]
        public async Task CreateOrUpdateFromExternalAsync_InvalidParameters_ShouldThrowArgumentException(
            string? subject, string? provider, int organizationId)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            var externalUser = new ExternalUserInfo
            {
                Subject = subject ?? "",
                Provider = provider ?? "",
                Email = "test@example.com"
            };

            await Assert.ThrowsAsync<ArgumentException>(() => 
                service.CreateOrUpdateFromExternalAsync(externalUser, organizationId));
        }

        [Fact]
        public async Task CreateOrUpdateFromExternalAsync_NullExternalUser_ShouldThrowArgumentNullException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var mockLogger = new Mock<ILogger<UserService>>();
            var service = new UserService(context, mockLogger.Object);

            await Assert.ThrowsAsync<ArgumentNullException>(() => 
                service.CreateOrUpdateFromExternalAsync(null!, 1));
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

        // ---- 境界の検証（EcAuthDocs#164 段 2。#122 で解決方法を作り替える前の安全網）----

        private static async Task SeedUserWithMappingAsync(EcAuthDbContext context, string tenantName = "test-tenant")
        {
            context.Organizations.Add(new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = tenantName });
            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };
            context.EcAuthUsers.Add(user);
            context.ExternalIdpMappings.Add(new ExternalIdpMapping
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google",
                ExternalSubject = "google-user-123"
            });
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task GetUserByExternalIdAsync_SameSubjectWithDifferentProvider_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedUserWithMappingAsync(context);
            var service = new UserService(context, new Mock<ILogger<UserService>>().Object);

            // provider が違えば、sub が同じでも別人として扱う
            var result = await service.GetUserByExternalIdAsync("line", "google-user-123", 1);

            Assert.Null(result);
        }

        [Fact]
        public async Task GetUserByExternalIdAsync_FromAnotherTenant_ShouldReturnNull()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var seedContext = TestDbContextHelper.CreateInMemoryContext(dbName))
            {
                await SeedUserWithMappingAsync(seedContext);
            }

            // 同じ DB を別テナントのリクエストとして開く。Organization ID が一致していても、
            // EcAuthUser / ExternalIdpMapping のクエリフィルター（Organization.TenantName）で見えない
            var otherTenant = new MockTenantService();
            otherTenant.SetTenant("other-tenant");
            using var context = TestDbContextHelper.CreateInMemoryContext(dbName, otherTenant);
            var service = new UserService(context, new Mock<ILogger<UserService>>().Object);

            Assert.Null(await service.GetUserByExternalIdAsync("google", "google-user-123", 1));
            Assert.Null(await service.GetUserBySubjectAsync("test-subject"));
        }

        [Fact]
        public async Task GetUserByExternalIdAsync_InDeletedOrganization_ShouldReturnNull()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var seedContext = TestDbContextHelper.CreateInMemoryContext(dbName))
            {
                await SeedUserWithMappingAsync(seedContext);
                var org = await seedContext.Organizations.SingleAsync();
                org.DeletedAt = DateTimeOffset.UtcNow;
                await seedContext.SaveChangesAsync();
            }

            using var context = TestDbContextHelper.CreateInMemoryContext(dbName);
            var service = new UserService(context, new Mock<ILogger<UserService>>().Object);

            // 論理削除された Organization のユーザーはクエリフィルターで見えない
            Assert.Null(await service.GetUserByExternalIdAsync("google", "google-user-123", 1));
        }

        [Fact]
        public async Task CreateOrUpdateFromExternalAsync_ExistingUser_ShouldUpdateEmailHashWithoutNewMapping()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedUserWithMappingAsync(context);
            var service = new UserService(context, new Mock<ILogger<UserService>>().Object);

            var result = await service.CreateOrUpdateFromExternalAsync(new ExternalUserInfo
            {
                Provider = "google",
                Subject = "google-user-123",
                Email = "changed@example.com"
            }, 1);

            Assert.Equal("test-subject", result.Subject);
            Assert.Equal(EmailHashUtil.HashEmail("changed@example.com"), result.EmailHash);
            Assert.Single(await context.EcAuthUsers.ToListAsync());
            Assert.Single(await context.ExternalIdpMappings.ToListAsync());
        }
    }
}

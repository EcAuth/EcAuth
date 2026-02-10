using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    public class B2BUserServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly B2BUserService _service;
        private readonly Mock<ILogger<B2BUserService>> _mockLogger;
        private readonly Organization _organization;

        public B2BUserServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockLogger = new Mock<ILogger<B2BUserService>>();
            _service = new B2BUserService(_context, _mockLogger.Object);

            // テスト用のテナントをセットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            };

            _context.Organizations.Add(_organization);
            _context.SaveChanges();
        }

        #region CreateAsync Tests

        [Fact]
        public async Task CreateAsync_ValidRequest_ShouldCreateUser()
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                ExternalId = "admin@example.com",
                UserType = "admin",
                OrganizationId = 1
            };

            // Act
            var result = await _service.CreateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.User);
            Assert.NotEmpty(result.User.Subject);
            Assert.Equal(request.ExternalId, result.User.ExternalId);
            Assert.Equal(request.UserType, result.User.UserType);
            Assert.Equal(request.OrganizationId, result.User.OrganizationId);
            Assert.True(result.User.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.True(result.User.UpdatedAt <= DateTimeOffset.UtcNow);

            // DBに保存されていることを確認
            var saved = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == result.User.Subject);
            Assert.NotNull(saved);
        }

        [Fact]
        public async Task CreateAsync_WithoutExternalId_ShouldCreateUser()
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                ExternalId = null,
                UserType = "staff",
                OrganizationId = 1
            };

            // Act
            var result = await _service.CreateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Null(result.User.ExternalId);
            Assert.Equal("staff", result.User.UserType);
        }

        [Fact]
        public async Task CreateAsync_ShouldGenerateUniqueSubject()
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                UserType = "admin",
                OrganizationId = 1
            };

            // Act
            var subjects = new HashSet<string>();
            for (int i = 0; i < 5; i++)
            {
                var result = await _service.CreateAsync(request);
                Assert.True(subjects.Add(result.User.Subject), "重複したSubjectが生成されました");
            }

            // Assert
            Assert.Equal(5, subjects.Count);
        }

        [Fact]
        public async Task CreateAsync_SubjectShouldBeValidUuid()
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                UserType = "admin",
                OrganizationId = 1
            };

            // Act
            var result = await _service.CreateAsync(request);

            // Assert
            Assert.True(Guid.TryParse(result.User.Subject, out _), "SubjectはUUID形式である必要があります");
        }

        [Fact]
        public async Task CreateAsync_WithExplicitSubject_ShouldUseProvidedSubject()
        {
            // Arrange
            var explicitSubject = "550e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BUserService.CreateUserRequest
            {
                Subject = explicitSubject,
                UserType = "admin",
                OrganizationId = 1
            };

            // Act
            var result = await _service.CreateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(explicitSubject, result.User.Subject);
        }

        [Fact]
        public async Task CreateAsync_WithoutSubject_ShouldAutoGenerateUuid()
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                Subject = null,
                UserType = "admin",
                OrganizationId = 1
            };

            // Act
            var result = await _service.CreateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(Guid.TryParse(result.User.Subject, out _), "自動生成されたSubjectはUUID形式である必要があります");
        }

        [Theory]
        [InlineData("not-a-uuid")]
        [InlineData("12345")]
        [InlineData("xyz-invalid")]
        public async Task CreateAsync_InvalidUuidSubject_ShouldThrowArgumentException(string invalidSubject)
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                Subject = invalidSubject,
                UserType = "admin",
                OrganizationId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateAsync(request));
            Assert.Contains("UUID", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task CreateAsync_InvalidOrganizationId_ShouldThrowArgumentException(int organizationId)
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                UserType = "admin",
                OrganizationId = organizationId
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateAsync(request));
            Assert.Contains("OrganizationId", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateAsync_EmptyUserType_ShouldThrowArgumentException(string userType)
        {
            // Arrange
            var request = new IB2BUserService.CreateUserRequest
            {
                UserType = userType,
                OrganizationId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateAsync(request));
            Assert.Contains("UserType", ex.Message);
        }

        #endregion

        #region GetBySubjectAsync Tests

        [Fact]
        public async Task GetBySubjectAsync_ExistingUser_ShouldReturn()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "test-subject-123",
                ExternalId = "admin@example.com",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetBySubjectAsync("test-subject-123");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("test-subject-123", result.Subject);
            Assert.Equal("admin@example.com", result.ExternalId);
        }

        [Fact]
        public async Task GetBySubjectAsync_NonExisting_ShouldReturnNull()
        {
            // Act
            var result = await _service.GetBySubjectAsync("non-existing-subject");

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetBySubjectAsync_InvalidSubject_ShouldReturnNull(string? subject)
        {
            // Act
            var result = await _service.GetBySubjectAsync(subject!);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region GetByExternalIdAsync Tests

        [Fact]
        public async Task GetByExternalIdAsync_ExistingUser_ShouldReturn()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "test-subject-456",
                ExternalId = "unique-external-id",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetByExternalIdAsync("unique-external-id", 1);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("unique-external-id", result.ExternalId);
            Assert.Equal("test-subject-456", result.Subject);
        }

        [Fact]
        public async Task GetByExternalIdAsync_DifferentOrganization_ShouldReturnNull()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "test-subject-789",
                ExternalId = "shared-external-id",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetByExternalIdAsync("shared-external-id", 999);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task GetByExternalIdAsync_InvalidExternalId_ShouldReturnNull(string? externalId)
        {
            // Act
            var result = await _service.GetByExternalIdAsync(externalId!, 1);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region UpdateAsync Tests

        [Fact]
        public async Task UpdateAsync_ExistingUser_ShouldUpdateFields()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "update-test-subject",
                ExternalId = "old-external-id",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            var request = new IB2BUserService.UpdateUserRequest
            {
                Subject = "update-test-subject",
                ExternalId = "new-external-id",
                UserType = "staff"
            };

            // Act
            var result = await _service.UpdateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("new-external-id", result.ExternalId);
            Assert.Equal("staff", result.UserType);
            Assert.True(result.UpdatedAt >= user.CreatedAt);
        }

        [Fact]
        public async Task UpdateAsync_PartialUpdate_ShouldOnlyUpdateProvidedFields()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "partial-update-subject",
                ExternalId = "original-external-id",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            var request = new IB2BUserService.UpdateUserRequest
            {
                Subject = "partial-update-subject",
                ExternalId = "updated-external-id",
                UserType = null // 更新しない
            };

            // Act
            var result = await _service.UpdateAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("updated-external-id", result.ExternalId);
            Assert.Equal("admin", result.UserType); // 変更されていない
        }

        [Fact]
        public async Task UpdateAsync_NonExistingUser_ShouldReturnNull()
        {
            // Arrange
            var request = new IB2BUserService.UpdateUserRequest
            {
                Subject = "non-existing-subject",
                ExternalId = "new-external-id"
            };

            // Act
            var result = await _service.UpdateAsync(request);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task UpdateAsync_InvalidSubject_ShouldThrowArgumentException(string? subject)
        {
            // Arrange
            var request = new IB2BUserService.UpdateUserRequest
            {
                Subject = subject!,
                ExternalId = "new-external-id"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.UpdateAsync(request));
            Assert.Contains("Subject", ex.Message);
        }

        #endregion

        #region DeleteAsync Tests

        [Fact]
        public async Task DeleteAsync_ExistingUser_ShouldDeleteAndReturnTrue()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "delete-test-subject",
                ExternalId = "to-be-deleted",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.DeleteAsync("delete-test-subject");

            // Assert
            Assert.True(result);

            var deleted = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == "delete-test-subject");
            Assert.Null(deleted);
        }

        [Fact]
        public async Task DeleteAsync_NonExistingUser_ShouldReturnFalse()
        {
            // Act
            var result = await _service.DeleteAsync("non-existing-subject");

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task DeleteAsync_InvalidSubject_ShouldReturnFalse(string? subject)
        {
            // Act
            var result = await _service.DeleteAsync(subject!);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region ExistsAsync Tests

        [Fact]
        public async Task ExistsAsync_ExistingUser_ShouldReturnTrue()
        {
            // Arrange
            var user = new B2BUser
            {
                Subject = "exists-test-subject",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.ExistsAsync("exists-test-subject");

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task ExistsAsync_NonExistingUser_ShouldReturnFalse()
        {
            // Act
            var result = await _service.ExistsAsync("non-existing-subject");

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task ExistsAsync_InvalidSubject_ShouldReturnFalse(string? subject)
        {
            // Act
            var result = await _service.ExistsAsync(subject!);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region CountByOrganizationAsync Tests

        [Fact]
        public async Task CountByOrganizationAsync_WithUsers_ShouldReturnCount()
        {
            // Arrange
            var users = new[]
            {
                new B2BUser { Subject = "user-1", UserType = "admin", OrganizationId = 1, Organization = _organization },
                new B2BUser { Subject = "user-2", UserType = "staff", OrganizationId = 1, Organization = _organization },
                new B2BUser { Subject = "user-3", UserType = "staff", OrganizationId = 1, Organization = _organization }
            };
            _context.B2BUsers.AddRange(users);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CountByOrganizationAsync(1);

            // Assert
            Assert.Equal(3, result);
        }

        [Fact]
        public async Task CountByOrganizationAsync_NoUsers_ShouldReturnZero()
        {
            // Act
            var result = await _service.CountByOrganizationAsync(999);

            // Assert
            Assert.Equal(0, result);
        }

        [Fact]
        public async Task CountByOrganizationAsync_ShouldOnlyCountOrganizationUsers()
        {
            // Arrange
            var org2 = new Organization
            {
                Id = 2,
                Code = "org-2",
                Name = "組織2",
                TenantName = "tenant-2"
            };
            _context.Organizations.Add(org2);

            var users = new[]
            {
                new B2BUser { Subject = "org1-user-1", UserType = "admin", OrganizationId = 1, Organization = _organization },
                new B2BUser { Subject = "org1-user-2", UserType = "staff", OrganizationId = 1, Organization = _organization },
                new B2BUser { Subject = "org2-user-1", UserType = "admin", OrganizationId = 2, Organization = org2 }
            };
            _context.B2BUsers.AddRange(users);
            await _context.SaveChangesAsync();

            // Act
            var org1Count = await _service.CountByOrganizationAsync(1);
            var org2Count = await _service.CountByOrganizationAsync(2);

            // Assert
            Assert.Equal(2, org1Count);
            Assert.Equal(1, org2Count);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}

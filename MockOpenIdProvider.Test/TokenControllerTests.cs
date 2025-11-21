using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MockOpenIdProvider.Controllers;
using MockOpenIdProvider.Models;
using MockOpenIdProvider.Services;
using Moq;
using Xunit;
using System.Linq;

namespace MockOpenIdProvider.Test
{
    public class TokenControllerTests
    {
        /// <summary>
        /// インメモリDBを設定してDbContextを作成する
        /// </summary>
        private IdpDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<IdpDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            // OrganizationServiceのモックを作成
            var mockOrganizationService = new Mock<IOrganizationService>();
            mockOrganizationService.Setup(os => os.TenantName).Returns("dev");

            var context = new IdpDbContext(options, mockOrganizationService.Object);

            return context;
        }

        /// <summary>
        /// テスト用のクライアントとユーザーデータを作成する
        /// </summary>
        private async Task SeedTestData(IdpDbContext context)
        {
            // テストOrganization作成
            var organization = new Organization
            {
                Name = "Development",
                TenantName = "dev",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.Organizations.AddAsync(organization);
            await context.SaveChangesAsync();

            // テストクライアント作成
            var client = new Client
            {
                ClientId = "test_client_id",
                ClientSecret = "test_client_secret",
                ClientName = "Test Client",
                RedirectUri = "https://localhost/callback",
                PublicKey = "test_public_key",
                PrivateKey = "test_private_key",
                OrganizationId = organization.Id
            };
            await context.Clients.AddAsync(client);
            await context.SaveChangesAsync();

            // テストユーザー作成
            var user = new MockIdpUser
            {
                Email = "test@example.com",
                Password = "password",
                ClientId = client.Id,
                OrganizationId = organization.Id
            };
            await context.Users.AddAsync(user);
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task Index_AuthorizationCodeGrant_Success()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);

            var client = await context.Clients.FirstAsync();
            var user = await context.Users.FirstAsync();
            
            var now = DateTime.UtcNow;
            var expireTime = (int)(now.AddHours(1).Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

            // 有効な認可コードを作成
            var organization = await context.Organizations.FirstAsync();
            var authCode = new AuthorizationCode
            {
                Code = "valid_auth_code",
                CreatedAt = now,
                ExpiresIn = expireTime,
                Used = false,
                ClientId = client.Id,
                Client = client,
                UserId = user.Id,
                User = user,
                OrganizationId = organization.Id
            };
            await context.AuthorizationCodes.AddAsync(authCode);
            await context.SaveChangesAsync();

            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "authorization_code", 
                "valid_auth_code", 
                "https://localhost/callback", 
                "test_client_id", 
                "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var tokenResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));

            Assert.NotNull(tokenResponse);
            Assert.True(tokenResponse.ContainsKey("access_token"));
            Assert.True(tokenResponse.ContainsKey("token_type"));
            Assert.True(tokenResponse.ContainsKey("expires_in"));
            Assert.True(tokenResponse.ContainsKey("refresh_token"));
            Assert.Equal("Bearer", tokenResponse["token_type"].ToString());            // 認可コードが使用済みになっていることを確認
            var updatedAuthCode = await context.AuthorizationCodes.FindAsync(authCode.Id);
            Assert.NotNull(updatedAuthCode);
            Assert.True(updatedAuthCode!.Used);
            
            // アクセストークンが保存されていることを確認（グローバルクエリフィルターを無視）
            var accessToken = await context.AccessTokens.IgnoreQueryFilters().FirstOrDefaultAsync();
            Assert.NotNull(accessToken);

            // リフレッシュトークンが保存されていることを確認（グローバルクエリフィルターを無視）
            var refreshToken = await context.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync();
            Assert.NotNull(refreshToken);
        }
        
        [Fact]
        public async Task Index_InvalidClient_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);
            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "authorization_code", 
                "some_code", 
                "https://localhost/callback", 
                "invalid_client_id", 
                "invalid_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_client", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_InvalidAuthCode_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);
            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "authorization_code", 
                "invalid_code", 
                "https://localhost/callback", 
                "test_client_id", 
                "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_grant", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_UsedAuthCode_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);

            var client = await context.Clients.FirstAsync();
            var user = await context.Users.FirstAsync();
            
            var now = DateTime.UtcNow;
            var expireTime = (int)(now.AddHours(1).Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

            // 使用済みの認可コードを作成
            var organization = await context.Organizations.FirstAsync();
            var authCode = new AuthorizationCode
            {
                Code = "used_auth_code",
                CreatedAt = now,
                ExpiresIn = expireTime,
                Used = true,  // 既に使用済み
                ClientId = client.Id,
                Client = client,
                UserId = user.Id,
                User = user,
                OrganizationId = organization.Id
            };
            await context.AuthorizationCodes.AddAsync(authCode);
            await context.SaveChangesAsync();

            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "authorization_code", 
                "used_auth_code", 
                "https://localhost/callback", 
                "test_client_id", 
                "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_grant", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_ExpiredAuthCode_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);

            var client = await context.Clients.FirstAsync();
            var user = await context.Users.FirstAsync();
            
            var now = DateTime.UtcNow;
            // 過去の時間を設定
            var expireTime = (int)(now.AddHours(-1).Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

            // 期限切れの認可コードを作成
            var organization = await context.Organizations.FirstAsync();
            var authCode = new AuthorizationCode
            {
                Code = "expired_auth_code",
                CreatedAt = now.AddHours(-2),
                ExpiresIn = expireTime,  // 期限切れ
                Used = false,
                ClientId = client.Id,
                Client = client,
                UserId = user.Id,
                User = user,
                OrganizationId = organization.Id
            };
            await context.AuthorizationCodes.AddAsync(authCode);
            await context.SaveChangesAsync();

            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "authorization_code", 
                "expired_auth_code", 
                "https://localhost/callback", 
                "test_client_id", 
                "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_grant", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_RefreshTokenGrant_Success()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);

            var client = await context.Clients.FirstAsync();
            var user = await context.Users.FirstAsync();
            
            var now = DateTime.UtcNow;
            var expireTime = (int)(now.AddDays(30).Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

            // 有効なリフレッシュトークンを作成
            var organization = await context.Organizations.FirstAsync();
            var refreshToken = new RefreshToken
            {
                Token = "valid_refresh_token",
                CreatedAt = now,
                ExpiresIn = expireTime,
                ClientId = client.Id,
                Client = client,
                UserId = user.Id,
                User = user,
                OrganizationId = organization.Id
            };
            await context.RefreshTokens.AddAsync(refreshToken);
            await context.SaveChangesAsync();

            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "refresh_token", 
                refresh_token: "valid_refresh_token", 
                client_id: "test_client_id", 
                client_secret: "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var tokenResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(tokenResponse);
            Assert.True(tokenResponse.ContainsKey("access_token"));
            Assert.True(tokenResponse.ContainsKey("token_type"));
            Assert.True(tokenResponse.ContainsKey("expires_in"));
            Assert.True(tokenResponse.ContainsKey("refresh_token"));
            Assert.Equal("Bearer", tokenResponse["token_type"].ToString());
            Assert.Equal("valid_refresh_token", tokenResponse["refresh_token"].ToString());

            // 新しいアクセストークンが保存されていることを確認（グローバルクエリフィルターを無視）
            var accessToken = await context.AccessTokens.IgnoreQueryFilters().FirstOrDefaultAsync();
            Assert.NotNull(accessToken);
        }
        
        [Fact]
        public async Task Index_InvalidRefreshToken_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);
            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "refresh_token", 
                refresh_token: "invalid_refresh_token", 
                client_id: "test_client_id", 
                client_secret: "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_grant", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_ExpiredRefreshToken_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);

            var client = await context.Clients.FirstAsync();
            var user = await context.Users.FirstAsync();
            
            var now = DateTime.UtcNow;
            // 過去の時間を設定
            var expireTime = (int)(now.AddDays(-1).Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

            // 期限切れのリフレッシュトークンを作成
            var organization = await context.Organizations.FirstAsync();
            var refreshToken = new RefreshToken
            {
                Token = "expired_refresh_token",
                CreatedAt = now.AddDays(-30),
                ExpiresIn = expireTime,  // 期限切れ
                ClientId = client.Id,
                Client = client,
                UserId = user.Id,
                User = user,
                OrganizationId = organization.Id
            };
            await context.RefreshTokens.AddAsync(refreshToken);
            await context.SaveChangesAsync();

            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "refresh_token", 
                refresh_token: "expired_refresh_token", 
                client_id: "test_client_id", 
                client_secret: "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("invalid_grant", errorResponse["error"].ToString());
            
            // 期限切れのリフレッシュトークンが削除されていることを確認（グローバルクエリフィルターを無視）
            var tokenCount = await context.RefreshTokens.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, tokenCount);
        }
        
        [Fact]
        public async Task Index_UnsupportedGrantType_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);
            var controller = new TokenController(context);

            // Act
            var result = await controller.Index(
                "unsupported_grant_type", 
                client_id: "test_client_id", 
                client_secret: "test_client_secret"
            );

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            var errorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult.Value));
            
            Assert.NotNull(errorResponse);
            Assert.True(errorResponse.ContainsKey("error"));
            Assert.Equal("unsupported_grant_type", errorResponse["error"].ToString());
        }
        
        [Fact]
        public async Task Index_MissingClientCredentials_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            var controller = new TokenController(context);

            // Act - クライアントIDが空の場合
            var result1 = await controller.Index(
                "authorization_code", 
                "some_code", 
                "https://localhost/callback", 
                null, 
                "test_client_secret"
            );
            
            // Act - クライアントシークレットが空の場合
            var result2 = await controller.Index(
                "authorization_code", 
                "some_code", 
                "https://localhost/callback", 
                "test_client_id", 
                null
            );

            // Assert
            var jsonResult1 = Assert.IsType<JsonResult>(result1);
            var errorResponse1 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult1.Value));
            
            Assert.NotNull(errorResponse1);
            Assert.True(errorResponse1.ContainsKey("error"));
            Assert.Equal("invalid_request", errorResponse1["error"].ToString());
            
            var jsonResult2 = Assert.IsType<JsonResult>(result2);
            var errorResponse2 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult2.Value));
            
            Assert.NotNull(errorResponse2);
            Assert.True(errorResponse2.ContainsKey("error"));
            Assert.Equal("invalid_request", errorResponse2["error"].ToString());
        }
        
        [Fact]
        public async Task Index_MissingRequiredParams_ReturnsError()
        {
            // Arrange
            var context = CreateDbContext();
            await SeedTestData(context);
            var controller = new TokenController(context);

            // Act - authorization_code グラントでcodeが無い場合
            var result1 = await controller.Index(
                "authorization_code", 
                null, 
                "https://localhost/callback", 
                "test_client_id", 
                "test_client_secret"
            );
            
            // Act - authorization_code グラントでredirect_uriが無い場合
            var result2 = await controller.Index(
                "authorization_code", 
                "some_code", 
                null, 
                "test_client_id", 
                "test_client_secret"
            );
            
            // Act - refresh_token グラントでrefresh_tokenが無い場合
            var result3 = await controller.Index(
                "refresh_token", 
                null, 
                null, 
                "test_client_id", 
                "test_client_secret",
                null
            );

            // Assert
            var jsonResult1 = Assert.IsType<JsonResult>(result1);
            var errorResponse1 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult1.Value));
            
            Assert.NotNull(errorResponse1);
            Assert.True(errorResponse1.ContainsKey("error"));
            Assert.Equal("invalid_request", errorResponse1["error"].ToString());
            
            var jsonResult2 = Assert.IsType<JsonResult>(result2);
            var errorResponse2 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult2.Value));
            
            Assert.NotNull(errorResponse2);
            Assert.True(errorResponse2.ContainsKey("error"));
            Assert.Equal("invalid_request", errorResponse2["error"].ToString());
            
            var jsonResult3 = Assert.IsType<JsonResult>(result3);
            var errorResponse3 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(jsonResult3.Value));
            
            Assert.NotNull(errorResponse3);
            Assert.True(errorResponse3.ContainsKey("error"));
            Assert.Equal("invalid_request", errorResponse3["error"].ToString());
        }
    }
}

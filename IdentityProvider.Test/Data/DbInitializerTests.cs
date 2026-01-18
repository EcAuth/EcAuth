using IdentityProvider.Data;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Data
{
    public class DbInitializerTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly Mock<ILogger<DbInitializer>> _mockLogger;

        public DbInitializerTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockLogger = new Mock<ILogger<DbInitializer>>();
        }

        #region Seeder Execution Order Tests

        [Fact]
        public async Task InitializeAsync_ShouldExecuteSeedersInOrder()
        {
            // Arrange
            var executionOrder = new List<int>();

            var seeder1 = new TestSeeder("Migration1", 300, () => executionOrder.Add(300));
            var seeder2 = new TestSeeder("Migration1", 100, () => executionOrder.Add(100));
            var seeder3 = new TestSeeder("Migration1", 200, () => executionOrder.Add(200));

            // シーダーが実行されるようにマイグレーションを模擬
            // TestableDbInitializer は DbInitializer を継承し、
            // CanConnectAsync と GetAppliedMigrationsAsync をオーバーライドして制御可能
            var seeders = new List<IDbSeeder> { seeder1, seeder2, seeder3 };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string> { "Migration1" });

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.Equal(3, executionOrder.Count);
            Assert.Equal(100, executionOrder[0]);
            Assert.Equal(200, executionOrder[1]);
            Assert.Equal(300, executionOrder[2]);
        }

        [Fact]
        public async Task InitializeAsync_WithSameOrder_ShouldExecuteInRegistrationOrder()
        {
            // Arrange
            var executionOrder = new List<string>();

            var seeder1 = new TestSeeder("Migration1", 100, () => executionOrder.Add("First"));
            var seeder2 = new TestSeeder("Migration1", 100, () => executionOrder.Add("Second"));

            var seeders = new List<IDbSeeder> { seeder1, seeder2 };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string> { "Migration1" });

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.Equal(2, executionOrder.Count);
            Assert.Equal("First", executionOrder[0]);
            Assert.Equal("Second", executionOrder[1]);
        }

        #endregion

        #region Migration Check Tests

        [Fact]
        public async Task InitializeAsync_ShouldSkipSeederWhenMigrationNotApplied()
        {
            // Arrange
            var wasExecuted = false;
            var seeder = new TestSeeder("NonExistentMigration", 100, () => wasExecuted = true);

            var seeders = new List<IDbSeeder> { seeder };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string>());

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.False(wasExecuted);
        }

        [Fact]
        public async Task InitializeAsync_ShouldRunSeederWhenMigrationApplied()
        {
            // Arrange
            var wasExecuted = false;
            var seeder = new TestSeeder("AppliedMigration", 100, () => wasExecuted = true);

            var seeders = new List<IDbSeeder> { seeder };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string> { "AppliedMigration" });

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.True(wasExecuted);
        }

        [Fact]
        public async Task InitializeAsync_WithMultipleSeeders_ShouldOnlyRunMatchingMigrations()
        {
            // Arrange
            var executedSeeders = new List<string>();

            var seeder1 = new TestSeeder("Migration1", 100, () => executedSeeders.Add("Seeder1"));
            var seeder2 = new TestSeeder("Migration2", 200, () => executedSeeders.Add("Seeder2"));
            var seeder3 = new TestSeeder("Migration3", 300, () => executedSeeders.Add("Seeder3"));

            var seeders = new List<IDbSeeder> { seeder1, seeder2, seeder3 };
            // Only Migration1 and Migration3 are applied
            var appliedMigrations = new HashSet<string> { "Migration1", "Migration3" };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, appliedMigrations);

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.Equal(2, executedSeeders.Count);
            Assert.Contains("Seeder1", executedSeeders);
            Assert.Contains("Seeder3", executedSeeders);
            Assert.DoesNotContain("Seeder2", executedSeeders);
        }

        #endregion

        #region Database Connection Tests

        [Fact]
        public async Task InitializeAsync_WhenDatabaseNotAvailable_ShouldSkipAllSeeders()
        {
            // Arrange
            var wasExecuted = false;
            var seeder = new TestSeeder("Migration1", 100, () => wasExecuted = true);

            var seeders = new List<IDbSeeder> { seeder };
            // canConnect = false をシミュレート
            var initializer = new TestableDbInitializer(
                seeders,
                _mockLogger.Object,
                new HashSet<string> { "Migration1" },
                canConnect: false);

            var configuration = new ConfigurationBuilder().Build();

            // Act
            await initializer.InitializeAsync(_context, configuration);

            // Assert
            Assert.False(wasExecuted);
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public async Task InitializeAsync_WhenSeederThrows_ShouldPropagateException()
        {
            // Arrange
            var seeder = new TestSeeder("Migration1", 100, () => throw new InvalidOperationException("Test exception"));

            var seeders = new List<IDbSeeder> { seeder };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string> { "Migration1" });

            var configuration = new ConfigurationBuilder().Build();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                initializer.InitializeAsync(_context, configuration));
        }

        [Fact]
        public async Task InitializeAsync_WhenSeederThrows_ShouldLogError()
        {
            // Arrange
            var seeder = new TestSeeder("Migration1", 100, () => throw new InvalidOperationException("Test exception"));

            var seeders = new List<IDbSeeder> { seeder };
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string> { "Migration1" });

            var configuration = new ConfigurationBuilder().Build();

            // Act
            try
            {
                await initializer.InitializeAsync(_context, configuration);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("TestSeeder")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region Empty Seeders Tests

        [Fact]
        public async Task InitializeAsync_WithNoSeeders_ShouldCompleteSuccessfully()
        {
            // Arrange
            var seeders = new List<IDbSeeder>();
            var initializer = new TestableDbInitializer(seeders, _mockLogger.Object, new HashSet<string>());

            var configuration = new ConfigurationBuilder().Build();

            // Act & Assert (should not throw)
            await initializer.InitializeAsync(_context, configuration);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }

        #region Test Helpers

        /// <summary>
        /// テスト用のシーダー実装
        /// </summary>
        private class TestSeeder : IDbSeeder
        {
            private readonly Action _onSeed;

            public string RequiredMigration { get; }
            public int Order { get; }

            public TestSeeder(string requiredMigration, int order, Action onSeed)
            {
                RequiredMigration = requiredMigration;
                Order = order;
                _onSeed = onSeed;
            }

            public Task SeedAsync(EcAuthDbContext context, IConfiguration configuration, ILogger logger)
            {
                _onSeed();
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// テスト用の DbInitializer 実装
        /// DbInitializer を継承し、CanConnectAsync と GetAppliedMigrationsAsync をオーバーライド
        /// </summary>
        private class TestableDbInitializer : DbInitializer
        {
            private readonly HashSet<string> _appliedMigrations;
            private readonly bool _canConnect;

            public TestableDbInitializer(
                IEnumerable<IDbSeeder> seeders,
                ILogger<DbInitializer> logger,
                HashSet<string> appliedMigrations,
                bool canConnect = true) : base(seeders, logger)
            {
                _appliedMigrations = appliedMigrations;
                _canConnect = canConnect;
            }

            protected override Task<bool> CanConnectAsync(EcAuthDbContext context)
                => Task.FromResult(_canConnect);

            protected override Task<HashSet<string>> GetAppliedMigrationsAsync(EcAuthDbContext context)
                => Task.FromResult(_appliedMigrations);
        }

        #endregion
    }
}

using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

public class SnapshotPathResolutionTests
{
    [Fact]
    public async Task Snapshot_restore_service_is_registered_with_host_startup()
    {
        ServiceDescriptor? registration = null;
        await using var parent = new WebApplicationFactory<Program>();
        await using var factory = parent.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.ConfigureServices(services =>
            {
                registration = services.FirstOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(SnapshotRestoreService)
                );
                services.RemoveAll<IHostedService>();
                if (registration is not null)
                {
                    services.Add(registration);
                }
            });
        });
        using var client = factory.CreateClient();

        _ = registration.Should().NotBeNull("Program must register snapshot restoration in the host lifecycle");
        _ = factory
            .Services.GetServices<IHostedService>()
            .Should()
            .ContainSingle(service => service is SnapshotRestoreService)
            .Which.Should()
            .BeOfType<SnapshotRestoreService>();
    }

    /// <summary>Proves the registered snapshot store rejects data persisted for a different GitHub owner.</summary>
    [Fact]
    public async Task Registered_snapshot_store_should_discard_a_snapshot_when_the_owner_changes()
    {
        var directory = Directory.CreateTempSubdirectory("fixportal-owner-snapshot-").FullName;
        var path = Path.Combine(directory, "snapshot.json");
        try
        {
            await using var parentFactory = new WebApplicationFactory<Program>();
            await using var originalFactory = parentFactory.WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Owner", "FixPortal");
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.UseSetting("Dashboard:SnapshotPath", path);
                _ = builder.UseSetting("Dashboard:RefreshSeconds", "60");
                _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });
            var originalStore = originalFactory.Services.GetRequiredService<IDashboardSnapshotStore>();
            await originalStore.SaveAsync(
                new DashboardSnapshot(Instant.FromUtc(2026, 7, 30, 12, 0), "FixPortal", [], [], null),
                TestContext.Current.CancellationToken
            );

            await using var changedFactory = parentFactory.WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Owner", "OtherOrg");
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.UseSetting("Dashboard:SnapshotPath", path);
                _ = builder.UseSetting("Dashboard:RefreshSeconds", "60");
                _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });
            var changedOwnerStore = changedFactory.Services.GetRequiredService<IDashboardSnapshotStore>();

            _ = (await changedOwnerStore.LoadAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Relative_snapshot_path_should_resolve_under_the_host_content_root()
    {
        var contentRoot = Directory.CreateTempSubdirectory("fixportal-cib18-").FullName;
        var relativeDirectory = $"snapshot-path-{Guid.NewGuid():N}";
        var relativePath = Path.Join(relativeDirectory, "snapshot.json");
        var contentRootPath = Path.Join(contentRoot, relativePath);
        var workingDirectoryPath = Path.Join(Environment.CurrentDirectory, relativePath);

        try
        {
            // WebApplicationFactory implements IAsyncDisposable; disposing it synchronously
            // blocks the host's async teardown on the calling thread.
            await using var parentFactory = new WebApplicationFactory<Program>();
            await using var factory = parentFactory.WithWebHostBuilder(builder =>
            {
                _ = builder.UseContentRoot(contentRoot);
                _ = builder.UseSetting("GitHub:Owner", "FixPortal");
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.UseSetting("Dashboard:SnapshotPath", relativePath);
                _ = builder.UseSetting("Dashboard:RefreshSeconds", "60");
                _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });
            var environment = factory.Services.GetRequiredService<IHostEnvironment>();
            var store = factory.Services.GetRequiredService<IDashboardSnapshotStore>();
            var snapshot = new DashboardSnapshot(Instant.FromUtc(2026, 7, 30, 12, 0), "FixPortal", [], [], null);

            await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);

            _ = environment.ContentRootPath.Should().Be(contentRoot);
            _ = File.Exists(contentRootPath).Should().BeTrue();
            _ = File.Exists(workingDirectoryPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            var workingDirectory = Path.GetDirectoryName(workingDirectoryPath)!;
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }
}

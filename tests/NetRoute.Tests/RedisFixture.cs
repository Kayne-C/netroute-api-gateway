using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace NetRoute.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("public.ecr.aws/docker/library/redis:8").Build();

    public string ConnectionString => _container.GetConnectionString();

    public IConnectionMultiplexer Connect() => ConnectionMultiplexer.Connect(ConnectionString);

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;

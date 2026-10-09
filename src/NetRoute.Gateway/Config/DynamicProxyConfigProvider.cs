using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace NetRoute.Gateway.Config;

public sealed class DynamicProxyConfigProvider : IProxyConfigProvider
{
    private sealed class Snapshot(GatewayState state) : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();
        public IReadOnlyList<RouteConfig> Routes { get; } = state.Routes;
        public IReadOnlyList<ClusterConfig> Clusters { get; } = state.Clusters;
        public IChangeToken ChangeToken { get; } = default!;
        public void Signal() => _cts.Cancel();
        public CancellationChangeToken Token => new(_cts.Token);
    }

    private sealed class Config(Snapshot snapshot) : IProxyConfig
    {
        public IReadOnlyList<RouteConfig> Routes => snapshot.Routes;
        public IReadOnlyList<ClusterConfig> Clusters => snapshot.Clusters;
        public IChangeToken ChangeToken => snapshot.Token;
    }

    private Snapshot _snapshot;

    public DynamicProxyConfigProvider(StateStore store)
    {
        _snapshot = new Snapshot(store.Current);
        store.Changed += state =>
        {
            var next = new Snapshot(state);
            var previous = Interlocked.Exchange(ref _snapshot, next);
            previous.Signal();
        };
    }

    public IProxyConfig GetConfig() => new Config(Volatile.Read(ref _snapshot));
}

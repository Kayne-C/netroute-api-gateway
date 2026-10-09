using System.Text.Json;
using NetRoute.Gateway.RateLimiting;

namespace NetRoute.Gateway.Config;

public sealed class StateStore
{
    private readonly object _gate = new();
    private readonly string? _persistPath;
    private GatewayState _state;
    private IReadOnlyDictionary<string, RateLimitPolicy> _policies;

    public StateStore(GatewayState initial, string? persistPath)
    {
        _state = initial;
        _persistPath = persistPath;
        _policies = Index(initial);
    }

    public event Action<GatewayState>? Changed;

    public GatewayState Current => Volatile.Read(ref _state);

    public bool TryGetPolicy(string name, out RateLimitPolicy policy)
        => Volatile.Read(ref _policies).TryGetValue(name, out policy!);

    public static StateStore Load(string seedFile, string? persistPath)
    {
        var source = persistPath is not null && File.Exists(persistPath) ? persistPath : seedFile;
        var state = File.Exists(source)
            ? JsonSerializer.Deserialize<GatewayState>(File.ReadAllText(source), GatewayState.Json) ?? new GatewayState()
            : new GatewayState();
        return new StateStore(state, persistPath);
    }

    public GatewayState Update(Func<GatewayState, GatewayState> mutate)
    {
        GatewayState next;
        lock (_gate)
        {
            next = mutate(_state);
            Persist(next);
            Volatile.Write(ref _state, next);
            Volatile.Write(ref _policies, Index(next));
        }

        Changed?.Invoke(next);
        return next;
    }

    private void Persist(GatewayState state)
    {
        if (_persistPath is null)
        {
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(_persistPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _persistPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, GatewayState.Json));
        File.Move(tmp, _persistPath, overwrite: true);
    }

    private static Dictionary<string, RateLimitPolicy> Index(GatewayState state)
        => state.RateLimitPolicies.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
}

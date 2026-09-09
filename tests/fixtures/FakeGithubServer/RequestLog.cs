namespace FakeGithubServer;

/// <summary>Records every request's path+query, in the order received, for one fake-server
/// instance. Registered as a DI singleton (see <c>Program.cs</c>) so each hosted instance --
/// typically one per test via its own <c>WebApplicationFactory&lt;Program&gt;</c> -- gets its own
/// isolated log; there is no shared static state for tests to race on.</summary>
public sealed class RequestLog
{
    private readonly List<string> _requests = [];
    private readonly object _gate = new();

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    public void Record(string pathAndQuery)
    {
        lock (_gate)
        {
            _requests.Add(pathAndQuery);
        }
    }
}

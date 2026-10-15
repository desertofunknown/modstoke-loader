using System;

namespace Modstoke.ClientCompanion;

// Bounded local disclosure for one current Bolt server connection. Attempted reads count, even if they fail.
internal sealed class AuditRateLimiter
{
    internal const int MaxRequestsPerSession = 6;
    internal const long MinimumIntervalMilliseconds = 60_000;

    private IntPtr _serverPointer;
    private uint _connectionId;
    private int _requests;
    private long _lastAt = long.MinValue;

    internal bool TryBegin(IntPtr serverPointer, uint connectionId, long now)
    {
        if (serverPointer == IntPtr.Zero) return false;
        if (serverPointer != _serverPointer || connectionId != _connectionId)
        {
            _serverPointer = serverPointer;
            _connectionId = connectionId;
            _requests = 0;
            _lastAt = long.MinValue;
        }
        if (_requests >= MaxRequestsPerSession ||
            (_lastAt != long.MinValue && now - _lastAt < MinimumIntervalMilliseconds)) return false;
        _requests++;
        _lastAt = now;
        return true;
    }
}

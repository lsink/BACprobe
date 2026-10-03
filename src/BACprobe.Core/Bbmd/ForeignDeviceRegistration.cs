using System.IO.BACnet;
using System.Net;

namespace BACprobe.Core.Bbmd;

/// <summary>The few BVLC operations registration needs, so the state machine can be tested without a network.</summary>
public interface IBvlcLink
{
    /// <summary>Raised for every BVLC-Result message received.</summary>
    event Action<IPEndPoint, BacnetBvlcResults>? ResultReceived;

    /// <summary>Send Register-Foreign-Device. False if the packet could not be sent.</summary>
    bool SendRegister(IPEndPoint bbmd, short ttlSeconds);

    void SendRemoteWhoIs(IPEndPoint bbmd, int low, int high);
}

/// <summary>Adapts the BACnet library's transport and client to <see cref="IBvlcLink"/>.</summary>
public sealed class LibraryBvlcLink : IBvlcLink, IDisposable
{
    private readonly BacnetIpUdpProtocolTransport _transport;
    private readonly BacnetClient _client;

    public LibraryBvlcLink(BacnetIpUdpProtocolTransport transport, BacnetClient client)
    {
        _transport = transport;
        _client = client;
        _transport.Bvlc.MessageReceived += OnMessage;
    }

    public event Action<IPEndPoint, BacnetBvlcResults>? ResultReceived;

    private void OnMessage(IPEndPoint sender, BacnetBvlcFunctions function, BacnetBvlcResults result, object? data)
    {
        if (function == BacnetBvlcFunctions.BVLC_RESULT) ResultReceived?.Invoke(sender, result);
    }

    public bool SendRegister(IPEndPoint bbmd, short ttlSeconds) => _transport.SendRegisterAsForeignDevice(bbmd, ttlSeconds);

    public void SendRemoteWhoIs(IPEndPoint bbmd, int low, int high) =>
        _client.RemoteWhoIs(bbmd.Address.ToString(), bbmd.Port, low, high);

    public void Dispose() => _transport.Bvlc.MessageReceived -= OnMessage;
}

public enum BbmdState { NotRegistered, Registering, Registered, Refused, NoAnswer, SendFailed }

/// <summary>
/// Registers this PC as a foreign device with a BBMD, reports the outcome in plain English, and renews
/// the registration before the TTL runs out.
/// </summary>
public sealed class ForeignDeviceRegistration : IDisposable
{
    private readonly IBvlcLink _link;
    private readonly Lock _lock = new();
    private TaskCompletionSource<BbmdState>? _pending;
    private Timer? _renewTimer;
    private bool _disposed;

    public ForeignDeviceRegistration(IBvlcLink link, BbmdTarget target)
    {
        _link = link;
        Target = target;
        RenewEvery = target.RenewInterval;
        _link.ResultReceived += OnResult;
    }

    public BbmdTarget Target { get; }
    public BbmdState State { get; private set; } = BbmdState.NotRegistered;
    public bool IsRegistered => State == BbmdState.Registered;

    /// <summary>Plain-English status, with a likely cause and next step whenever something went wrong.</summary>
    public string Message { get; private set; } = "Not registered.";

    /// <summary>Raised when the state changes after the first attempt, e.g. a renewal that failed.</summary>
    public event Action? Changed;

    /// <summary>How often to renew while registered (half the TTL by default).</summary>
    public TimeSpan RenewEvery { get; set; }

    /// <summary>After a renewal fails, how long to wait before trying again. Retries continue until it works or this is disposed.</summary>
    public TimeSpan RetryEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a renewal waits for the BBMD's answer.</summary>
    public TimeSpan RenewReplyTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public async Task<BbmdState> RegisterAsync(TimeSpan? timeout = null, int attempts = 2, CancellationToken ct = default)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(3);
        Set(BbmdState.Registering, $"Registering with BBMD {Target}...");
        var state = BbmdState.NoAnswer;
        for (var i = 0; i < attempts && state == BbmdState.NoAnswer; i++)
            state = await SendAndWaitAsync(wait, ct);

        Set(state, Describe(state, wait, attempts));
        if (state == BbmdState.Registered) ScheduleRenewal(RenewEvery);
        return state;
    }

    public void RemoteWhoIs(int low, int high) => _link.SendRemoteWhoIs(Target.EndPoint, low, high);

    private async Task<BbmdState> SendAndWaitAsync(TimeSpan wait, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BbmdState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pending = tcs;

        bool sent;
        try { sent = _link.SendRegister(Target.EndPoint, (short)Target.TtlSeconds); }
        catch (Exception) { sent = false; }
        if (!sent)
        {
            lock (_lock) _pending = null;
            return BbmdState.SendFailed;
        }

        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct));
            return done == tcs.Task ? tcs.Task.Result : BbmdState.NoAnswer;
        }
        catch (OperationCanceledException)
        {
            return BbmdState.NoAnswer;
        }
        finally
        {
            lock (_lock) _pending = null;
        }
    }

    private void OnResult(IPEndPoint sender, BacnetBvlcResults result)
    {
        if (!sender.Address.Equals(Target.Address)) return; // a different BBMD/peer, not our answer
        TaskCompletionSource<BbmdState>? pending;
        lock (_lock) pending = _pending;
        switch (result)
        {
            case BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION:
                pending?.TrySetResult(BbmdState.Registered);
                break;
            case BacnetBvlcResults.BVLC_RESULT_REGISTER_FOREIGN_DEVICE_NAK:
                pending?.TrySetResult(BbmdState.Refused);
                break;
        }
    }

    private void ScheduleRenewal(TimeSpan after)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _renewTimer?.Dispose();
            _renewTimer = new Timer(_ => _ = RenewAsync(), null, after, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RenewAsync()
    {
        if (_disposed) return;
        var wait = RenewReplyTimeout;
        var state = BbmdState.NoAnswer;
        for (var i = 0; i < 2 && state == BbmdState.NoAnswer; i++) state = await SendAndWaitAsync(wait, default);
        if (_disposed) return;

        if (state == BbmdState.Registered)
        {
            // Back after a failed renewal: say so. A routine renewal changes nothing the user can see.
            if (State != BbmdState.Registered)
                Set(state, $"Registration with BBMD {Target} restored. " + Describe(state, wait, 2));
            ScheduleRenewal(RenewEvery);
        }
        else
        {
            // Keep trying: a VPN reconnect or a short network drop should not cost the remote subnets for the rest of the visit.
            Set(state, "Lost the BBMD registration while renewing. " + Describe(state, wait, 2) +
                       $"\n  BACprobe keeps retrying every {RetryEvery.TotalSeconds:0} s.");
            ScheduleRenewal(RetryEvery);
        }
    }

    private string Describe(BbmdState state, TimeSpan wait, int attempts) => state switch
    {
        BbmdState.Registered =>
            $"Registered with BBMD {Target} for {Target.TtlSeconds}s (renews automatically).",
        BbmdState.Refused =>
            $"The BBMD {Target} refused the registration.\n" +
            "  Likely cause: foreign-device registration is switched off on that BBMD, or it only accepts certain addresses.\n" +
            "  Next step:    ask whoever runs the BBMD to allow your PC's IP (or enable foreign devices).",
        BbmdState.NoAnswer =>
            $"The BBMD {Target} did not answer ({attempts} tries, {wait.TotalSeconds:0}s each).\n" +
            $"  Likely cause: wrong IP or port, a firewall or VPN blocking UDP {Target.Port}, or that address is not a BBMD.\n" +
            "  Next step:    confirm the address with the site contact and that your PC can reach it (try ping).",
        BbmdState.SendFailed =>
            $"Could not send to the BBMD {Target}.\n" +
            "  Likely cause: no route to that network from the selected adapter.\n" +
            "  Next step:    check the adapter's gateway setting and that you are connected to the right network or VPN.",
        _ => Message,
    };

    private void Set(BbmdState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _renewTimer?.Dispose();
            _renewTimer = null;
        }
        _link.ResultReceived -= OnResult;
        (_link as IDisposable)?.Dispose();
    }
}

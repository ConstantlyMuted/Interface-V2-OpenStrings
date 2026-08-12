using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDP subscriber endpoint for Quest/Unity participant clients.
///
/// Control protocol:
/// Client -> Server: SUBSCRIBE
/// Server -> Client: SUBSCRIBED|playerIndex
/// Server -> Client: UPDATE|DUMMY|sequence|serverUnixMs
/// Client -> Server: UPDATE_RECEIVED|playerIndex|sequence
/// Client -> Server: UNSUBSCRIBE|playerIndex
/// Client -> Server: SPHERE_TRIGGERED|playerIndex|frequencyIndex|partialIndex|harmonic|frequencyHz|worldX|worldY|worldZ
/// Client -> Server: PIN_STATE|playerIndex|botID|status|stringID|partialIndex|localX|localY|localZ|localQx|localQy|localQz|localQw
/// Server -> Client: PIN_UPDATE|count|botID,status,stringID,partialIndex,x,y,z,qx,qy,qz,qw|botID,...   (full pin table, every server tick)
/// Server -> Client: COLOR_UPDATE|base64Bytes   (colorBytesAllPartials, only sent when it changes)
///
/// IMPORTANT: stringID/partialIndex identify which sphere a pin is snapped to (-1,-1 if none).
/// When valid, receivers should resolve the ACTUAL world position locally via
/// StringFrame3D.GetSphereWorldPosition(stringID, partialIndex) instead of trusting the
/// x/y/z fields — every device tracks its own copy of the shared frame (marker tracking),
/// so a raw world position sent by one device is meaningless on another. The x/y/z/quat
/// fields are FRAME-LOCAL (relative to StringFrame3D's own transform), used only as a
/// fallback for the Held state (not snapped to anything yet) — convert with
/// frame.transform.TransformPoint/rotation, never treat them as world space directly.
///
/// Real server update payload parsing is intentionally disabled until payloads are repaired.
/// </summary>
public class UdpSubscriptionClient : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private string serverIp = "192.168.0.100";
    [SerializeField] private int serverListenerPort = 5001;

    [Header("Client")]
    [Tooltip("0 = choose an available local UDP port. Same socket sends and receives.")]
    [SerializeField] private int localPort = 0;
    [SerializeField] private bool connectOnStart = true;

    [Header("Timing")]
    [SerializeField, Min(0.25f)] private float subscribeRetrySeconds = 2f;
    [SerializeField, Min(1f)] private float updateTimeoutSeconds = 30f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    public struct PlayerState
    {
        public int playerIndex;
        public bool held;
        public Vector3 position;
    }

    /// <summary>
    /// Pin status codes. Must stay in sync with the Java-side PIN_STATUS_* constants
    /// in TH_UDPBroadcaster. Extend by adding new values on both sides.
    /// </summary>
    public enum PinStatus
    {
        Unset = 0,
        Set = 1,
        Held = 2,
        Unavailable = 3,   // server-authoritative, never sent by client
        Playing = 4        // server-authoritative, never sent by client
    }

    public struct PinState
    {
        public int botIndex;
        public PinStatus status;

        /// <summary>Sphere this pin is snapped to, or -1 if not applicable (e.g. Held/Unset).</summary>
        public int stringID;
        public int partialIndex;
        public bool HasSphere => stringID >= 0 && partialIndex >= 0;

        /// <summary>Frame-local pose fallback — only meaningful when HasSphere is false.</summary>
        public Vector3 localPosition;
        public Quaternion localRotation;
    }

    public bool IsSubscribed { get; private set; }
    public int PlayerIndex { get; private set; } = -1;

    public PlayerState[] LatestStates { get; private set; } = new PlayerState[0];
    public float LatestStandInFloat { get; private set; } = 0f;

    /// <summary>Latest known state of every pin/bot, keyed by botIndex.</summary>
    public IReadOnlyDictionary<int, PinState> LatestPinStates => pinStatesByBotId;
    private readonly Dictionary<int, PinState> pinStatesByBotId = new Dictionary<int, PinState>();

    /// <summary>Latest colorBytesAllPartials payload from the server. Null until first received.</summary>
    public byte[] LatestColorBytes { get; private set; } = null;

    /// <summary>Fired on the main thread the next Update() after a new PIN_UPDATE is parsed.</summary>
    public event Action OnPinStatesUpdated;

    /// <summary>Fired on the main thread the next Update() after a new COLOR_UPDATE is parsed.</summary>
    public event Action OnColorBytesUpdated;

    private volatile bool pinStatesDirty = false;
    private volatile bool colorBytesDirty = false;

    private UdpClient udpClient;
    private IPEndPoint serverEndpoint;
    private Thread receiverThread;
    private volatile bool running;

    private readonly object stateLock = new object();
    private DateTime lastSubscribeAttemptUtc = DateTime.MinValue;
    private DateTime lastUpdateReceivedUtc = DateTime.MinValue;

    [Header("Dummy Player (Testing)")]
    [SerializeField] private bool useDummyPlayer = false;
    [SerializeField] private Vector3 dummyPlayerPosition = new Vector3(1, 1, 1);
    [SerializeField] private bool dummyPlayerHeld = true;

    public PlayerState[] GetStatesWithDummy()
    {
        if (!useDummyPlayer)
            return LatestStates;

        var dummy = new PlayerState
        {
            playerIndex = 999,
            held = dummyPlayerHeld,
            position = dummyPlayerPosition
        };

        var combined = new PlayerState[LatestStates.Length + 1];
        System.Array.Copy(LatestStates, combined, LatestStates.Length);
        combined[LatestStates.Length] = dummy;
        return combined;
    }


    private void Start()
    {
        if (connectOnStart)
            BeginClient();
    }

    public void BeginClient()
    {
        if (running)
            return;

        if (!IPAddress.TryParse(serverIp, out IPAddress serverAddress))
        {
            Debug.LogError("[UDP Subscribe] Invalid Server IP: " + serverIp);
            return;
        }

        serverEndpoint = new IPEndPoint(serverAddress, serverListenerPort);

        try
        {
            udpClient = new UdpClient(localPort);
            udpClient.Client.ReceiveTimeout = 1000;
            running = true;

            receiverThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "UDP Subscription Receiver"
            };
            receiverThread.Start();

            if (showDebugLogs)
            {
                int boundPort = ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
                Debug.Log("[UDP Subscribe] Client started. Server=" + serverEndpoint +
                          ", localPort=" + boundPort);
            }

            SendSubscribe();
        }
        catch (Exception e)
        {
            Debug.LogError("[UDP Subscribe] Failed to start client: " + e.Message);
            StopClient(false);
        }
    }

    private void Update()
    {
        if (!running)
            return;

        // Events must fire on the main thread; the receiver thread only flags dirty.
        if (pinStatesDirty)
        {
            pinStatesDirty = false;
            OnPinStatesUpdated?.Invoke();
        }

        if (colorBytesDirty)
        {
            colorBytesDirty = false;
            OnColorBytesUpdated?.Invoke();
        }

        DateTime now = DateTime.UtcNow;
        bool subscribed;
        DateTime lastSubscribeAttempt;
        DateTime lastUpdate;

        lock (stateLock)
        {
            subscribed = IsSubscribed;
            lastSubscribeAttempt = lastSubscribeAttemptUtc;
            lastUpdate = lastUpdateReceivedUtc;
        }

        if (!subscribed)
        {
            if ((now - lastSubscribeAttempt).TotalSeconds >= subscribeRetrySeconds)
                SendSubscribe();

            return;
        }

        if ((now - lastUpdate).TotalSeconds > updateTimeoutSeconds)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning("[UDP Subscribe] No update for " +
                                 updateTimeoutSeconds + " seconds. Re-subscribing.");
            }

            lock (stateLock)
            {
                IsSubscribed = false;
            }

            SendSubscribe();
        }
    }

    private void SendSubscribe()
    {
        lock (stateLock)
        {
            lastSubscribeAttemptUtc = DateTime.UtcNow;
        }

        SendText("SUBSCRIBE");

        if (showDebugLogs)
            Debug.Log("[UDP Subscribe] Sent SUBSCRIBE.");
    }

    private void ReceiveLoop()
    {
        IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remoteEndpoint);

                if (!remoteEndpoint.Address.Equals(serverEndpoint.Address))
                    continue;

                HandleServerDatagram(data);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.TimedOut &&
                    e.SocketErrorCode != SocketError.Interrupted &&
                    running)
                {
                    Debug.LogError("[UDP Subscribe] Socket error: " + e.Message);
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                if (running)
                    Debug.LogError("[UDP Subscribe] Receive error: " + e.Message);
            }
        }
    }

    private void HandleServerDatagram(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data).Trim();

        if (text.StartsWith("SUBSCRIBED|", StringComparison.Ordinal))
        {
            string[] parts = text.Split('|');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int playerIndex))
            {
                lock (stateLock)
                {
                    PlayerIndex = playerIndex;
                    IsSubscribed = true;
                    lastUpdateReceivedUtc = DateTime.UtcNow;
                }

                if (showDebugLogs)
                    Debug.Log("[UDP Subscribe] Confirmed. Player index=" + playerIndex);
            }

            return;
        }

        if (text.StartsWith("UPDATE|", StringComparison.Ordinal))
        {
            string[] parts = text.Split('|');
            string sequence = parts.Length >= 3 ? parts[2] : "-1";

            int playerIndex;
            lock (stateLock)
            {
                if (!IsSubscribed)
                    return;

                playerIndex = PlayerIndex;
                lastUpdateReceivedUtc = DateTime.UtcNow;
            }

            // HandleApplicationUpdate(data); // disabled: real update payloads still broken.
            SendText("UPDATE_RECEIVED|" + playerIndex + "|" + sequence);

            if (showDebugLogs)
                Debug.Log("[UDP Subscribe] Dummy update ACKed. sequence=" + sequence);

            return;
        }

        if (text.StartsWith("STATE_UPDATE|", StringComparison.Ordinal))
        {
            ParseStateUpdate(text);

            int playerIndex;
            lock (stateLock)
            {
                if (!IsSubscribed)
                    return;

                playerIndex = PlayerIndex;
                lastUpdateReceivedUtc = DateTime.UtcNow;
            }

            SendText("UPDATE_RECEIVED|" + playerIndex + "|-1");

            if (showDebugLogs)
                Debug.Log("[UDP Subscribe] State update ACKed. players=" + LatestStates.Length);

            return;
        }

        if (text.StartsWith("PIN_UPDATE|", StringComparison.Ordinal))
        {
            ParsePinUpdate(text);

            lock (stateLock)
            {
                if (!IsSubscribed)
                    return;

                lastUpdateReceivedUtc = DateTime.UtcNow;
            }

            // Liveness only; no per-message ACK protocol for pin updates (they arrive every tick).
            if (showDebugLogs)
                Debug.Log("[UDP Subscribe] Pin update received. pins=" + pinStatesByBotId.Count);

            return;
        }

        if (text.StartsWith("COLOR_UPDATE|", StringComparison.Ordinal))
        {
            ParseColorUpdate(text);

            lock (stateLock)
            {
                if (!IsSubscribed)
                    return;

                lastUpdateReceivedUtc = DateTime.UtcNow;
            }

            if (showDebugLogs)
                Debug.Log("[UDP Subscribe] Color update received. bytes=" +
                          (LatestColorBytes != null ? LatestColorBytes.Length : 0));

            return;
        }

        // Future compatibility path: a non-control datagram counts as a received update.
        // Parsing remains disabled, but server liveness ACK still functions.
        int subscribedPlayerIndex;
        lock (stateLock)
        {
            if (!IsSubscribed)
                return;

            subscribedPlayerIndex = PlayerIndex;
            lastUpdateReceivedUtc = DateTime.UtcNow;
        }

        // HandleApplicationUpdate(data); // disabled until binary payload format is fixed.
        SendText("UPDATE_RECEIVED|" + subscribedPlayerIndex + "|-1");

        if (showDebugLogs)
            Debug.Log("[UDP Subscribe] Binary update ACKed; handling remains disabled.");
    }

    private void ParseStateUpdate(string text)
    {
        string[] parts = text.Split('|');
        if (parts.Length < 3) return;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float standIn))
            return;
        if (!int.TryParse(parts[2], out int count))
            return;

        var states = new PlayerState[count];
        for (int i = 0; i < count; i++)
        {
            string[] fields = parts[3 + i].Split(',');
            if (fields.Length < 5) continue;

            states[i] = new PlayerState
            {
                playerIndex = int.Parse(fields[0], CultureInfo.InvariantCulture),
                held = fields[1] == "1",
                position = new Vector3(
                    float.Parse(fields[2], CultureInfo.InvariantCulture),
                    float.Parse(fields[3], CultureInfo.InvariantCulture),
                    float.Parse(fields[4], CultureInfo.InvariantCulture))
            };
        }

        LatestStandInFloat = standIn;
        LatestStates = states;
    }

    /// <summary>
    /// Parses "PIN_UPDATE|count|botID,status,x,y,z,qx,qy,qz,qw|botID,...".
    /// Full table every time — replaces pinStatesByBotId wholesale so pins removed
    /// server-side (not currently supported, but future-proof) also disappear here.
    /// </summary>
    private void ParsePinUpdate(string text)
    {
        string[] parts = text.Split('|');
        if (parts.Length < 2) return;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            return;

        if (parts.Length < 2 + count) return;

        var updated = new Dictionary<int, PinState>(count);
        for (int i = 0; i < count; i++)
        {
            string[] fields = parts[2 + i].Split(',');
            if (fields.Length < 11) continue;

            try
            {
                int botIndex = int.Parse(fields[0], CultureInfo.InvariantCulture);
                var state = new PinState
                {
                    botIndex = botIndex,
                    status = (PinStatus)int.Parse(fields[1], CultureInfo.InvariantCulture),
                    stringID = int.Parse(fields[2], CultureInfo.InvariantCulture),
                    partialIndex = int.Parse(fields[3], CultureInfo.InvariantCulture),
                    localPosition = new Vector3(
                        float.Parse(fields[4], CultureInfo.InvariantCulture),
                        float.Parse(fields[5], CultureInfo.InvariantCulture),
                        float.Parse(fields[6], CultureInfo.InvariantCulture)),
                    localRotation = new Quaternion(
                        float.Parse(fields[7], CultureInfo.InvariantCulture),
                        float.Parse(fields[8], CultureInfo.InvariantCulture),
                        float.Parse(fields[9], CultureInfo.InvariantCulture),
                        float.Parse(fields[10], CultureInfo.InvariantCulture))
                };
                updated[botIndex] = state;
            }
            catch (FormatException)
            {
                // Skip malformed entry, keep parsing the rest.
            }
        }

        lock (stateLock)
        {
            pinStatesByBotId.Clear();
            foreach (var kvp in updated)
                pinStatesByBotId[kvp.Key] = kvp.Value;
        }

        pinStatesDirty = true;
    }

    /// <summary>Parses "COLOR_UPDATE|base64Bytes" (colorBytesAllPartials).</summary>
    private void ParseColorUpdate(string text)
    {
        int separatorIndex = text.IndexOf('|');
        if (separatorIndex < 0 || separatorIndex + 1 >= text.Length) return;

        string base64 = text.Substring(separatorIndex + 1);

        try
        {
            LatestColorBytes = Convert.FromBase64String(base64);
            colorBytesDirty = true;
        }
        catch (FormatException e)
        {
            if (showDebugLogs)
                Debug.LogWarning("[UDP Subscribe] Failed to decode COLOR_UPDATE payload: " + e.Message);
        }
    }

    public void SendSphereSelected(
        int botIndex,
        int frequencyIndex,
        int partialIndex)
    {
        int playerIndex;
        bool subscribed;

        lock (stateLock)
        {
            playerIndex = PlayerIndex;
            subscribed = IsSubscribed;
        }

        if (!running || !subscribed || playerIndex < 0)
            return;

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "SPHERE_SELECTED|{0}|{1}|{2}",
            playerIndex,
            frequencyIndex,
            partialIndex
        );

        SendText(message);

        if (showDebugLogs)
            Debug.Log("[UDP Subscribe] Sent sphere selected: " + message);
    }


    public void SendSphereTriggered(
        int frequencyIndex,
        int partialIndex,
        float frequencyHz,
        int harmonic,
        Vector3 worldPosition)
    {
        int playerIndex;
        bool subscribed;

        lock (stateLock)
        {
            playerIndex = PlayerIndex;
            subscribed = IsSubscribed;
        }

        if (!running || !subscribed || playerIndex < 0)
            return;

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "SPHERE_TRIGGERED|{0}|{1}|{2}|{3}|{4:F3}|{5:F4}|{6:F4}|{7:F4}",
            playerIndex,
            frequencyIndex,
            partialIndex,
            harmonic,
            frequencyHz,
            worldPosition.x,
            worldPosition.y,
            worldPosition.z
        );

        SendText(message);

        if (showDebugLogs)
            Debug.Log("[UDP Subscribe] Sent sphere trigger: " + message);
    }

    private void SendText(string message)
    {
        if (!running || udpClient == null || serverEndpoint == null)
            return;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            udpClient.Send(bytes, bytes.Length, serverEndpoint);
        }
        catch (Exception e)
        {
            if (running)
                Debug.LogError("[UDP Subscribe] Send failed: " + e.Message);
        }
    }

    private void OnApplicationQuit()
    {
        StopClient(true);
    }

    private void OnDestroy()
    {
        StopClient(true);
    }

    private void StopClient(bool sendUnsubscribe)
    {
        if (!running)
            return;

        if (sendUnsubscribe)
        {
            int playerIndex;
            bool subscribed;

            lock (stateLock)
            {
                playerIndex = PlayerIndex;
                subscribed = IsSubscribed;
            }

            if (subscribed && playerIndex >= 0)
                SendText("UNSUBSCRIBE|" + playerIndex);
        }

        running = false;

        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        if (receiverThread != null && receiverThread.IsAlive)
            receiverThread.Join(250);

        lock (stateLock)
        {
            IsSubscribed = false;
        }
    }

    public void SendState(bool held, Vector3 pos)
    {
        int playerIndex;
        bool subscribed;
        lock (stateLock)
        {
            playerIndex = PlayerIndex;
            subscribed = IsSubscribed;
        }
        if (!running || !subscribed || playerIndex < 0)
            return;

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "STATE|{0}|{1}|{2:F4}|{3:F4}|{4:F4}",
            playerIndex,
            held ? 1 : 0,
            held ? pos.x : 0f,
            held ? pos.y : 0f,
            held ? pos.z : 0f
        );

        SendText(message);
    }

    /// <summary>
    /// Reports a pin's (bot's) status to the server. Only Unset/Set/Held should be sent from
    /// a client — Unavailable/Playing are server-authoritative and get applied via the
    /// backend's updatePinStatus() hook, then propagate here through PIN_UPDATE.
    ///
    /// Pass stringID/partialIndex (>=0) when the pin is snapped to a sphere — receivers will
    /// resolve the exact world position locally rather than trusting a transmitted coordinate.
    /// Pass -1,-1 with localPos/localRot (relative to the StringFrame3D transform, NOT world
    /// space) when there's no sphere yet, e.g. while just being held.
    /// </summary>
    public void SendPinState(int botIndex, PinStatus status, int stringID, int partialIndex, Vector3 localPos, Quaternion localRot)
    {
        int playerIndex;
        bool subscribed;
        lock (stateLock)
        {
            playerIndex = PlayerIndex;
            subscribed = IsSubscribed;
        }
        if (!running || !subscribed || playerIndex < 0)
            return;

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "PIN_STATE|{0}|{1}|{2}|{3}|{4}|{5:F4}|{6:F4}|{7:F4}|{8:F4}|{9:F4}|{10:F4}|{11:F4}",
            playerIndex,
            botIndex,
            (int)status,
            stringID,
            partialIndex,
            localPos.x, localPos.y, localPos.z,
            localRot.x, localRot.y, localRot.z, localRot.w
        );

        SendText(message);

        if (showDebugLogs)
            Debug.Log("[UDP Subscribe] Sent pin state: " + message);
    }

    /// <summary>Convenience lookup; returns false if the pin/bot hasn't been reported yet.</summary>
    public bool TryGetPinState(int botIndex, out PinState state)
    {
        lock (stateLock)
        {
            return pinStatesByBotId.TryGetValue(botIndex, out state);
        }
    }

    /*
    private void HandleApplicationUpdate(byte[] data)
    {
        // Intentionally disabled/stubbed:
        // real server payload formats are currently broken and remain commented out.
    }
    */
}
using System;
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

    public bool IsSubscribed { get; private set; }
    public int PlayerIndex { get; private set; } = -1;

    public PlayerState[] LatestStates { get; private set; } = new PlayerState[0];
    public float LatestStandInFloat { get; private set; } = 0f;

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

    /*
    private void HandleApplicationUpdate(byte[] data)
    {
        // Intentionally disabled/stubbed:
        // real server payload formats are currently broken and remain commented out.
    }
    */
}

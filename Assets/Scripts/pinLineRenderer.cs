using System.Collections.Generic;
using UnityEngine;

public class PlayerConnectionLines : MonoBehaviour
{
    [SerializeField] private UdpSubscriptionClient client;
    [SerializeField] private StringFrame3D sphereSource;
    [SerializeField] private Transform[] localHeldTransforms;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float lineWidth = 0.01f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    private readonly List<LineRenderer> pool = new List<LineRenderer>();
    private int lineIndex = 0;

    private void Update()
    {
        lineIndex = 0;

        if (client == null || sphereSource == null || localHeldTransforms == null || localHeldTransforms.Length == 0)
        {
            SetActiveCount(0);
            return;
        }

        var pinStates = client.LatestPinStates;

        if (showDebugLogs)
        {
            Debug.Log($"[PlayerConnectionLines] SetPinCount={pinStates.Count}");
        }

        // Draw lines from each locally-held grabbable to every currently Set/snapped pin.
        for (int g = 0; g < localHeldTransforms.Length; g++)
        {
            Transform grabbable = localHeldTransforms[g];
            if (grabbable == null)
                continue;

            SnapGrabbable snap = grabbable.GetComponent<SnapGrabbable>();
            if (snap == null)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[PlayerConnectionLines] Grabbable {g} has no SnapGrabbable component!");
                continue;
            }

            bool isHeld = snap.IsHeld;
            if (showDebugLogs)
                Debug.Log($"[PlayerConnectionLines] Grabbable {g} ({grabbable.name}): IsHeld={isHeld}");

            if (!isHeld)
                continue;

            Vector3 origin = grabbable.position;

            foreach (var kvp in pinStates)
            {
                int botId = kvp.Key;
                UdpSubscriptionClient.PinState state = kvp.Value;

                if (state.status != UdpSubscriptionClient.PinStatus.Set || !state.HasSphere)
                    continue;

                if (botId == snap.botIndex)
                    continue; // don't draw a line to itself

                // Resolve the exact, live sphere position locally rather than trusting a
                // transmitted coordinate — matches how StringFrame3D positions the pin itself.
                Vector3 targetPos = sphereSource.GetSphereWorldPosition(state.stringID, state.partialIndex);

                LineRenderer lr = GetOrCreate(lineIndex);
                lr.SetPosition(0, origin);
                lr.SetPosition(1, targetPos);
                lr.gameObject.SetActive(true);

                if (showDebugLogs)
                    Debug.Log($"[PlayerConnectionLines]   Drew line {lineIndex}: {origin} -> pin {botId} @ {targetPos}");

                lineIndex++;
            }
        }

        SetActiveCount(lineIndex);
    }

    private LineRenderer GetOrCreate(int index)
    {
        if (index < pool.Count)
            return pool[index];

        var go = new GameObject("ConnectionLine_" + index);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        if (lineMaterial != null)
            lr.material = lineMaterial;

        pool.Add(lr);
        return lr;
    }

    private void SetActiveCount(int used)
    {
        for (int i = used; i < pool.Count; i++)
            pool[i].gameObject.SetActive(false);
    }
}
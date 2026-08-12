using System.Collections.Generic;
using UnityEngine;

public class PlayerConnectionLines : MonoBehaviour
{
    public enum StateMode { Play, Test }

    [SerializeField] private UdpSubscriptionClient client;
    [SerializeField] private Transform[] localHeldTransforms;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float lineWidth = 0.01f;

    [Header("Debug")]
    [SerializeField] private StateMode stateMode = StateMode.Play;
    [SerializeField] private bool showDebugLogs = true;

    private readonly List<LineRenderer> pool = new List<LineRenderer>();
    private int lineIndex = 0;

    private void Update()
    {
        lineIndex = 0;

        if (client == null || localHeldTransforms == null || localHeldTransforms.Length == 0)
        {
            SetActiveCount(0);
            return;
        }

        var states = stateMode == StateMode.Test
            ? client.GetStatesWithDummy()
            : client.LatestStates;

        int localPlayerIndex = client.PlayerIndex;

        if (showDebugLogs)
        {
            Debug.Log($"[PlayerConnectionLines] Mode={stateMode}, LocalPlayerIndex={localPlayerIndex}, StateCount={states.Length}");
        }

        if (showDebugLogs)
        {
            for (int i = 0; i < states.Length; i++)
            {
                Debug.Log($"[PlayerConnectionLines] State {i}: playerIndex={states[i].playerIndex}");
            }
        }

        // Draw lines from each local grabbable to all held remote objects
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

            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                if (showDebugLogs)
                    Debug.Log($"[PlayerConnectionLines]   State {i}: playerIndex={s.playerIndex}, held={s.held}, pos={s.position}");

                if (!s.held) continue;
                if (s.playerIndex == localPlayerIndex)
                {
                    if (showDebugLogs)
                        Debug.Log($"[PlayerConnectionLines]   Skipping self (playerIndex={localPlayerIndex})");
                    continue;
                }

                LineRenderer lr = GetOrCreate(lineIndex);
                lr.SetPosition(0, origin);
                lr.SetPosition(1, s.position);
                lr.gameObject.SetActive(true);

                if (showDebugLogs)
                    Debug.Log($"[PlayerConnectionLines]   Drew line {lineIndex}: {origin} -> {s.position}");

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

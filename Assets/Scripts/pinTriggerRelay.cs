using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class SphereTriggerRelay : MonoBehaviour
{
    [Header("Pin-Referenz (wird von StringFrame3D gesetzt)")]
    public StringFrame3D owner;
    public int stringID;
    public int partialIndex;

    [Header("Cooldown")]
    public float repeatCooldownSeconds = 0.25f;

    public float lastTriggerTime = -999f;

    private void Awake()
    {
        // Stelle sicher, dass der Collider ein Trigger ist
        var col = GetComponent<SphereCollider>();
        col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        TryTrigger(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryTrigger(other);
    }

    private void TryTrigger(Collider other)
    {
        if (Time.time - lastTriggerTime < repeatCooldownSeconds) return;
        if (owner == null) return;
        if (other == null) return;                          
        if (!string.IsNullOrEmpty(owner.playerTag) &&
            !other.CompareTag(owner.playerTag))
            return;

        int flatIndex = stringID * owner.config.nPartials + partialIndex;
        if (!owner.IsSphereOccupied(flatIndex)) return;
        
        lastTriggerTime = Time.time;
        owner.NotifySphereTriggered(stringID, partialIndex, other);
    }
}
using UnityEngine;

public class StringFrameSphereTrigger : MonoBehaviour
{
    [SerializeField] private float repeatCooldownSeconds = 0.25f;

    private StringFrame3D owner;
    private int stringID;
    private int partialIndex;
    private float lastTriggerTime = -999f;

    public void Initialize(StringFrame3D owner, int stringID, int partialIndex)
    {
        this.owner = owner;
        this.stringID = stringID;
        this.partialIndex = partialIndex;
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
        if (owner == null)
            return;

        if (!owner.CanTriggerSphere(stringID, partialIndex))
            return;

        if (Time.time - lastTriggerTime < repeatCooldownSeconds)
            return;

        lastTriggerTime = Time.time;
        owner.NotifySphereTriggered(stringID, partialIndex, other);
    }
}

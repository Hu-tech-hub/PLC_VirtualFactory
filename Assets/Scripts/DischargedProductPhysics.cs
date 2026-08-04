using UnityEngine;

[DisallowMultipleComponent]
public sealed class DischargedProductPhysics : MonoBehaviour
{
    private Rigidbody productRigidbody;
    private bool activationPending;
    private float activationTime;
    private int fixedStepsBeforeActivation;
    private Vector3 pendingLinearVelocity;
    private Vector3 pendingAngularVelocity;

    public bool PhysicsActive =>
        productRigidbody != null && !productRigidbody.isKinematic && productRigidbody.useGravity;

    public void Prepare(
        float activationDelay,
        Vector3 initialLinearVelocity,
        Vector3 initialAngularVelocity,
        float mass)
    {
        if (productRigidbody == null)
        {
            productRigidbody = GetComponent<Rigidbody>();
        }

        if (productRigidbody == null)
        {
            productRigidbody = gameObject.AddComponent<Rigidbody>();
        }

        activationPending = true;
        activationTime = Time.time + Mathf.Max(0f, activationDelay);
        pendingLinearVelocity = initialLinearVelocity;
        pendingAngularVelocity = initialAngularVelocity * Mathf.Deg2Rad;

        productRigidbody.mass = Mathf.Max(0.0001f, mass);
        productRigidbody.isKinematic = true;
        productRigidbody.useGravity = false;
        productRigidbody.detectCollisions = false;
        productRigidbody.linearVelocity = Vector3.zero;
        productRigidbody.angularVelocity = Vector3.zero;
        productRigidbody.position = transform.position;
        productRigidbody.rotation = transform.rotation;
        Physics.SyncTransforms();
        productRigidbody.detectCollisions = true;
        fixedStepsBeforeActivation = 1;

        enabled = true;
    }

    public void DeactivateForPool()
    {
        activationPending = false;
        if (productRigidbody != null)
        {
            productRigidbody.isKinematic = true;
            productRigidbody.useGravity = false;
            productRigidbody.detectCollisions = false;
            productRigidbody.linearVelocity = Vector3.zero;
            productRigidbody.angularVelocity = Vector3.zero;
        }

        gameObject.SetActive(false);
    }

    private void FixedUpdate()
    {
        if (!activationPending)
        {
            return;
        }

        if (fixedStepsBeforeActivation > 0)
        {
            fixedStepsBeforeActivation--;
            return;
        }

        if (Time.time < activationTime)
        {
            return;
        }

        activationPending = false;
        productRigidbody.detectCollisions = true;
        productRigidbody.isKinematic = false;
        productRigidbody.useGravity = true;
        productRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        productRigidbody.linearVelocity = pendingLinearVelocity;
        productRigidbody.angularVelocity = pendingAngularVelocity;
        enabled = false;
    }

    private void OnDisable()
    {
        activationPending = false;
    }
}

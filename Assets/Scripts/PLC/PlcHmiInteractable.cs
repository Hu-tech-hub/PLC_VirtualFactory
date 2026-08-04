using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public sealed class PlcHmiInteractable : MonoBehaviour
{
    public enum ControlKind
    {
        Start,
        Stop,
        ModeSelector,
        InspectionOk,
        InspectionNg,
        EmergencyStop,
        EmergencyRelease,
        ResetDisabled
    }

    public enum SelectorState
    {
        Unknown,
        Off,
        Manual,
        Auto
    }

    [SerializeField] private ControlKind kind;
    [SerializeField] private Transform movingPart;
    [SerializeField] private Vector3 pressedLocalOffset = new Vector3(0f, -0.055f, 0f);
    [SerializeField, Min(0.02f)] private float pressDuration = 0.08f;
    [SerializeField] private float manualAngle = 42f;
    [SerializeField] private float autoAngle = -42f;

    private Vector3 releasedLocalPosition;
    private Quaternion releasedLocalRotation;
    private Coroutine pressAnimation;
    private bool initialized;
    private bool pending;
    private Renderer[] visualRenderers;
    private Color[] enabledColors;

    public ControlKind Kind => kind;
    public bool Available { get; private set; } = true;
    public bool IsAnimating => pressAnimation != null;

    private void Awake()
    {
        InitializePose();
    }

    public void PlayMomentaryPress()
    {
        InitializePose();
        if (pressAnimation != null)
        {
            StopCoroutine(pressAnimation);
        }
        pressAnimation = StartCoroutine(AnimateMomentaryPress());
    }

    public void SetPressed(bool pressed)
    {
        InitializePose();
        if (pressAnimation != null)
        {
            StopCoroutine(pressAnimation);
            pressAnimation = null;
        }

        if (movingPart != null)
        {
            movingPart.localPosition = releasedLocalPosition +
                (pressed ? pressedLocalOffset : Vector3.zero);
        }
    }

    public void SetLatched(bool latched)
    {
        InitializePose();
        if (pressAnimation != null)
        {
            StopCoroutine(pressAnimation);
            pressAnimation = null;
        }

        if (movingPart != null && kind == ControlKind.EmergencyStop)
        {
            movingPart.localPosition = releasedLocalPosition + (latched ? pressedLocalOffset : Vector3.zero);
            movingPart.localRotation = releasedLocalRotation;
        }
    }

    public void PlayLatchedRelease()
    {
        InitializePose();
        if (movingPart == null || kind != ControlKind.EmergencyStop)
        {
            return;
        }

        if (pressAnimation != null)
        {
            StopCoroutine(pressAnimation);
        }

        movingPart.localPosition = releasedLocalPosition + pressedLocalOffset;
        movingPart.localRotation = releasedLocalRotation;
        pressAnimation = StartCoroutine(AnimateLatchedRelease());
    }

    public void SetSelectorState(SelectorState state)
    {
        InitializePose();
        if (movingPart == null || kind != ControlKind.ModeSelector)
        {
            return;
        }

        float angle = state == SelectorState.Manual ? manualAngle : state == SelectorState.Auto ? autoAngle : 0f;
        Quaternion target = Quaternion.AngleAxis(angle, Vector3.up) * releasedLocalRotation;
        movingPart.localRotation = target;
    }

    public void SetPending(bool value)
    {
        InitializePose();
        if (pending == value)
        {
            return;
        }

        pending = value;
        ApplyVisualState();
    }

    public void SetAvailable(bool available)
    {
        InitializePose();
        if (Available == available)
        {
            return;
        }

        Available = available;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        for (int index = 0; index < visualRenderers.Length; index++)
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            visualRenderers[index].GetPropertyBlock(block);
            Color color = pending
                ? new Color(1f, 0.55f, 0.05f, 1f)
                : Available
                    ? enabledColors[index]
                    : enabledColors[index] * 0.28f;
            color.a = 1f;
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            visualRenderers[index].SetPropertyBlock(block);
        }
    }

    private IEnumerator AnimateMomentaryPress()
    {
        if (movingPart == null)
        {
            yield break;
        }

        Vector3 pressed = releasedLocalPosition + pressedLocalOffset;
        yield return AnimatePosition(movingPart.localPosition, pressed, pressDuration);
        yield return AnimatePosition(movingPart.localPosition, releasedLocalPosition, pressDuration);
        pressAnimation = null;
    }

    private IEnumerator AnimatePosition(Vector3 from, Vector3 to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            movingPart.localPosition = Vector3.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        movingPart.localPosition = to;
    }

    private IEnumerator AnimateLatchedRelease()
    {
        Quaternion twisted = Quaternion.AngleAxis(35f, Vector3.up) * releasedLocalRotation;
        float elapsed = 0f;
        while (elapsed < pressDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            movingPart.localRotation = Quaternion.Slerp(
                releasedLocalRotation,
                twisted,
                Mathf.Clamp01(elapsed / pressDuration));
            yield return null;
        }

        movingPart.localRotation = releasedLocalRotation;
        movingPart.localPosition = releasedLocalPosition;
        pressAnimation = null;
    }

    private void InitializePose()
    {
        if (initialized)
        {
            return;
        }

        if (movingPart == null)
        {
            movingPart = transform;
        }
        releasedLocalPosition = movingPart.localPosition;
        releasedLocalRotation = movingPart.localRotation;
        visualRenderers = GetComponentsInChildren<Renderer>(true);
        enabledColors = new Color[visualRenderers.Length];
        for (int index = 0; index < visualRenderers.Length; index++)
        {
            Material material = visualRenderers[index].sharedMaterial;
            enabledColors[index] = material != null && material.HasProperty("_BaseColor")
                ? material.GetColor("_BaseColor")
                : material != null ? material.color : Color.white;
        }
        initialized = true;
    }
}

using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public sealed class PlcHmiIndicator : MonoBehaviour
{
    public enum IndicatorKind
    {
        PlcConnected,
        CommMode,
        Run,
        Auto,
        Manual,
        InspectionRequest,
        InspectionOk,
        InspectionNg,
        Alarm
    }

    public enum LampState
    {
        Off,
        On,
        Stale
    }

    [SerializeField] private IndicatorKind kind;
    [SerializeField] private Color onColor = Color.green;
    [SerializeField] private Color offColor = new Color(0.12f, 0.14f, 0.15f);
    [SerializeField] private Color staleColor = new Color(1f, 0.55f, 0.05f);
    [SerializeField, Min(0f)] private float emissionIntensity = 3f;

    private Renderer lampRenderer;
    private Material runtimeMaterial;
    private LampState currentState = (LampState)(-1);

    public IndicatorKind Kind => kind;

    private void Awake()
    {
        lampRenderer = GetComponent<Renderer>();
        runtimeMaterial = new Material(lampRenderer.sharedMaterial);
        runtimeMaterial.EnableKeyword("_EMISSION");
        lampRenderer.material = runtimeMaterial;
        SetState(LampState.Off);
    }

    public void SetState(LampState state)
    {
        if (lampRenderer == null)
        {
            lampRenderer = GetComponent<Renderer>();
        }

        if (runtimeMaterial == null)
        {
            runtimeMaterial = new Material(lampRenderer.sharedMaterial);
            runtimeMaterial.EnableKeyword("_EMISSION");
            lampRenderer.material = runtimeMaterial;
        }

        if (currentState == state)
        {
            return;
        }

        currentState = state;
        Color color = state == LampState.On ? onColor : state == LampState.Stale ? staleColor : offColor;
        runtimeMaterial.color = color;
        runtimeMaterial.SetColor("_BaseColor", color);
        runtimeMaterial.SetColor("_EmissionColor", state == LampState.Off ? Color.black : color * emissionIntensity);
    }

    private void OnDestroy()
    {
        if (runtimeMaterial != null)
        {
            Destroy(runtimeMaterial);
        }
    }
}

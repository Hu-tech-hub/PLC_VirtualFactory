using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class PlcHmiController : MonoBehaviour
{
    private const int StartBit = 1 << 0;
    private const int StopBit = 1 << 1;
    private const int ResetPulseBit = 1 << 2;
    private const int ManualAutoMask = (1 << 3) | (1 << 4);
    private const int AutoBit = 1 << 3;
    private const int ManualBit = 1 << 4;
    private const int EmergencyStopBit = 1 << 13;
    private const int ResetRequestBit = 1 << 5;
    private const int ResetAckBit = 1 << 8;
    private const int ProcessD0Mask = 0x00FE;

    [Header("PLC")]
    [SerializeField] private PlcConnectionTest plcConnection;
    [SerializeField] private PlcProcessAdapter processAdapter;

    [Header("World Interaction")]
    [SerializeField] private Camera interactionCamera;
    [SerializeField] private string interactableLayerName = "HMIInteractable";
    [SerializeField, Min(1f)] private float interactionDistance = 30f;

    [Header("Timing")]
    [SerializeField, Min(1f)] private float staleAfterSeconds = 5f;
    [SerializeField, Min(0.5f)] private float writeResponseTimeoutSeconds = 5f;
    [SerializeField, Min(0.5f)] private float emergencyFeedbackTimeoutSeconds = 5f;
    [SerializeField, Range(0.1f, 0.2f)] private float resetPulseSeconds = 0.15f;
    [SerializeField, Min(0.5f)] private float resetRequestTimeoutSeconds = 10f;
    [SerializeField, Min(0.5f)] private float cylinderRetractTimeoutSeconds = 5f;

    private readonly Dictionary<long, WriteResult> completedWrites = new Dictionary<long, WriteResult>();
    private readonly Dictionary<long, WriteResult> completedD0Writes = new Dictionary<long, WriteResult>();
    private readonly HashSet<long> trackedResetD0Requests = new HashSet<long>();
    private readonly HashSet<long> abandonedWriteRequests = new HashSet<long>();
    private PlcHmiInteractable[] controls = Array.Empty<PlcHmiInteractable>();
    private PlcHmiIndicator[] indicators = Array.Empty<PlcHmiIndicator>();
    private Text monitorStep;
    private Text monitorStepStatus;
    private Text monitorTotal;
    private Text monitorOk;
    private Text monitorNg;
    private Text monitorAlarmCode;
    private Text monitorAlarmStatus;
    private Image monitorAlarmPanel;
    private float nextRefreshTime;
    private bool startPulsePending;
    private bool stopPulsePending;
    private bool modeWritePending;
    private PlcHmiInteractable.SelectorState requestedSelectorState =
        PlcHmiInteractable.SelectorState.Unknown;
    private PlcHmiInteractable.SelectorState lastConfirmedSelectorState =
        PlcHmiInteractable.SelectorState.Unknown;
    private bool emergencyWritePending;
    private bool requestedEmergencyActive;
    private bool lastConfirmedEmergencyActive;
    private bool hasConfirmedEmergencyState;
    private PlcHmiInteractable heldMomentaryControl;
    private PlcHmiInteractable visualPressedControl;
    private int heldMomentaryBit;
    private bool heldMomentaryReleaseRequested;
    private bool heldMomentaryOffQueued;
    private ResetState resetState = ResetState.Idle;
    private long resetRequestId;
    private float resetStateDeadline;
    private float resetPulseReleaseAt;
    private bool resetPulseMayBeOn;
    private bool resetRequestOnObserved;
    private bool resetFaultLogged;
    private string resetFaultReason = string.Empty;
    private PlcHmiInteractable resetControl;

    private struct WriteResult
    {
        public int Result;
        public int Value;
    }

    private enum ConfirmedMode
    {
        Unknown,
        Off,
        Manual,
        Auto
    }

    private enum ResetState
    {
        Idle,
        SendingResetPulseOn,
        HoldingResetPulse,
        SendingResetPulseOff,
        WaitingForPlcResetRequest,
        QuiescingProcess,
        WaitingForExistingD0Writes,
        RetractingCylinder,
        RestoringProduct,
        ClearingProcessBits,
        VerifyingSafeState,
        SettingResetAck,
        WaitingForPlcRequestOff,
        ClearingResetAck,
        VerifyingResetComplete,
        RecoveringResetPulseOff,
        Faulted
    }

    private void Awake()
    {
        if (plcConnection == null)
        {
            plcConnection = FindAnyObjectByType<PlcConnectionTest>();
        }

        if (interactionCamera == null)
        {
            interactionCamera = Camera.main;
        }

        if (processAdapter == null)
        {
            processAdapter = FindAnyObjectByType<PlcProcessAdapter>();
        }

        CachePanelParts();
        RefreshPanel();
    }

    private void OnEnable()
    {
        if (plcConnection != null)
        {
            plcConnection.D0WriteRequestCompleted -= OnD0WriteRequestCompleted;
            plcConnection.D0WriteRequestCompleted += OnD0WriteRequestCompleted;
            plcConnection.D101WriteCompleted -= OnD101WriteCompleted;
            plcConnection.D101WriteCompleted += OnD101WriteCompleted;
        }
    }

    private void OnDisable()
    {
        if (plcConnection != null)
        {
            ForceResetPulseOffBestEffort();
            plcConnection.D0WriteRequestCompleted -= OnD0WriteRequestCompleted;
            plcConnection.D101WriteCompleted -= OnD101WriteCompleted;
        }

        ForceReleaseHeldMomentary();
        StopAllCoroutines();
        completedWrites.Clear();
        completedD0Writes.Clear();
        trackedResetD0Requests.Clear();
        abandonedWriteRequests.Clear();
        startPulsePending = false;
        stopPulsePending = false;
        modeWritePending = false;
        requestedSelectorState = PlcHmiInteractable.SelectorState.Unknown;
        foreach (PlcHmiInteractable control in controls)
        {
            if (control.Kind == PlcHmiInteractable.ControlKind.ModeSelector)
            {
                control.SetPending(false);
            }
        }
        emergencyWritePending = false;
        requestedEmergencyActive = false;
        foreach (PlcHmiInteractable control in controls)
        {
            if (control.Kind == PlcHmiInteractable.ControlKind.EmergencyStop)
            {
                control.SetPending(false);
            }
        }

        if (resetState != ResetState.Idle && processAdapter != null)
        {
            processAdapter.FaultSafetyReset();
        }
    }

    private void Update()
    {
        UpdateResetStateMachine();
        HandleWorldInteraction();
        if (Time.unscaledTime >= nextRefreshTime)
        {
            nextRefreshTime = Time.unscaledTime + 0.1f;
            RefreshPanel();
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            RequestMomentaryRelease();
            RequestEarlyResetPulseRelease();
        }
    }

    public bool TryActivateControl(PlcHmiInteractable control)
    {
        if (control == null || plcConnection == null)
        {
            return false;
        }

        if (IsResetBusy &&
            control.Kind != PlcHmiInteractable.ControlKind.EmergencyStop &&
            control.Kind != PlcHmiInteractable.ControlKind.Reset)
        {
            return false;
        }

        bool ready = plcConnection.Connected && plcConnection.D101Ready;
        switch (control.Kind)
        {
            case PlcHmiInteractable.ControlKind.Start:
                if (!CanStart() || startPulsePending)
                {
                    return false;
                }
                return BeginHeldCommand(control, StartBit, false);

            case PlcHmiInteractable.ControlKind.Stop:
                if (!ready || !IsCommunicationFresh() || stopPulsePending)
                {
                    return false;
                }
                return BeginHeldCommand(control, StopBit, true);

            case PlcHmiInteractable.ControlKind.ModeSelector:
                if (!ready || !IsCommunicationFresh() || !IsFreshDeviceOn("M400") || modeWritePending)
                {
                    return false;
                }

                PlcHmiInteractable.SelectorState current = GetConfirmedD101SelectorState();
                PlcHmiInteractable.SelectorState requested = GetNextSelectorState(current);
                if (requested == PlcHmiInteractable.SelectorState.Unknown)
                {
                    return false;
                }

                lastConfirmedSelectorState = current;
                modeWritePending = true;
                requestedSelectorState = requested;
                int target = requested == PlcHmiInteractable.SelectorState.Auto
                    ? AutoBit
                    : requested == PlcHmiInteractable.SelectorState.Manual
                        ? ManualBit
                        : 0;
                control.SetSelectorState(requested);
                control.SetPending(true);
                StartCoroutine(ModeWrite(control, target));
                return true;

            case PlcHmiInteractable.ControlKind.InspectionOk:
                if (!IsCommunicationFresh() || processAdapter == null ||
                    !processAdapter.TrySubmitInspectionResult(true))
                {
                    return false;
                }
                control.PlayMomentaryPress();
                return true;

            case PlcHmiInteractable.ControlKind.InspectionNg:
                if (!IsCommunicationFresh() || processAdapter == null ||
                    !processAdapter.TrySubmitInspectionResult(false))
                {
                    return false;
                }
                control.PlayMomentaryPress();
                return true;

            case PlcHmiInteractable.ControlKind.EmergencyStop:
                if (!ready || !IsCommunicationFresh() || !IsFreshDeviceOn("M400") ||
                    emergencyWritePending ||
                    !TryGetConfirmedEmergencyFeedback(out bool emergencyActive))
                {
                    return false;
                }

                lastConfirmedEmergencyActive = emergencyActive;
                hasConfirmedEmergencyState = true;
                requestedEmergencyActive = !emergencyActive;
                emergencyWritePending = true;
                control.SetPending(true);
                if (requestedEmergencyActive)
                {
                    control.SetLatched(true);
                }
                else
                {
                    control.PlayLatchedRelease();
                }

                StartCoroutine(EmergencyWrite(control, requestedEmergencyActive));
                return true;

            case PlcHmiInteractable.ControlKind.EmergencyRelease:
                return false;

            case PlcHmiInteractable.ControlKind.Reset:
                return TryStartResetPulse(control);

            default:
                return false;
        }
    }

    private void HandleWorldInteraction()
    {
        if (Mouse.current == null || interactionCamera == null)
        {
            return;
        }

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            RequestMomentaryRelease();
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame)
        {
            return;
        }

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return;
        }

        int layer = LayerMask.NameToLayer(interactableLayerName);
        if (layer < 0)
        {
            return;
        }

        Ray ray = interactionCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit, interactionDistance, 1 << layer,
                QueryTriggerInteraction.Ignore))
        {
            PlcHmiInteractable control = hit.collider.GetComponentInParent<PlcHmiInteractable>();
            if (control != null)
            {
                bool accepted = TryActivateControl(control);
                if (accepted &&
                    (control.Kind == PlcHmiInteractable.ControlKind.InspectionOk ||
                     control.Kind == PlcHmiInteractable.ControlKind.InspectionNg))
                {
                    visualPressedControl = control;
                    control.SetPressed(true);
                }
            }
        }
    }

    private bool BeginHeldCommand(PlcHmiInteractable control, int bit, bool highPriority)
    {
        bool isStart = bit == StartBit;
        SetPulsePending(isStart, true);
        heldMomentaryControl = control;
        heldMomentaryBit = bit;
        heldMomentaryReleaseRequested = false;
        heldMomentaryOffQueued = false;
        control.SetPressed(true);
        StartCoroutine(HeldCommand(control, bit, highPriority));
        return true;
    }

    private IEnumerator HeldCommand(PlcHmiInteractable control, int bit, bool highPriority)
    {
        bool isStart = bit == StartBit;
        if (!TryQueueD101(bit, bit, highPriority, out long onRequest))
        {
            CompleteHeldCommand(control, isStart);
            yield break;
        }

        yield return WaitForWrite(onRequest);
        if (!TakeWriteResult(onRequest, out WriteResult onResult) || onResult.Result != 0)
        {
            CompleteHeldCommand(control, isStart);
            yield break;
        }

        while (!heldMomentaryReleaseRequested && heldMomentaryControl == control &&
               plcConnection != null && plcConnection.Connected)
        {
            yield return null;
        }

        heldMomentaryOffQueued = true;
        if (!TryQueueD101(bit, 0, true, out long offRequest))
        {
            CompleteHeldCommand(control, isStart);
            yield break;
        }

        yield return WaitForWrite(offRequest);
        TakeWriteResult(offRequest, out _);
        CompleteHeldCommand(control, isStart);
    }

    private void RequestMomentaryRelease()
    {
        if (visualPressedControl != null)
        {
            visualPressedControl.SetPressed(false);
            visualPressedControl = null;
        }

        if (heldMomentaryControl != null)
        {
            heldMomentaryControl.SetPressed(false);
            heldMomentaryReleaseRequested = true;
        }
    }

    private void ForceReleaseHeldMomentary()
    {
        RequestMomentaryRelease();
        if (heldMomentaryControl == null)
        {
            return;
        }

        if (!heldMomentaryOffQueued && plcConnection != null)
        {
            plcConnection.TrySetD101MaskedBits(
                heldMomentaryBit, 0, false, out _);
        }

        SetPulsePending(heldMomentaryBit == StartBit, false);
        heldMomentaryControl = null;
        heldMomentaryBit = 0;
        heldMomentaryReleaseRequested = false;
        heldMomentaryOffQueued = false;
    }

    private void CompleteHeldCommand(PlcHmiInteractable control, bool isStart)
    {
        control.SetPressed(false);
        SetPulsePending(isStart, false);
        if (heldMomentaryControl == control)
        {
            heldMomentaryControl = null;
            heldMomentaryBit = 0;
            heldMomentaryReleaseRequested = false;
            heldMomentaryOffQueued = false;
        }
    }

    public string ResetStateName => resetState.ToString();
    public bool ResetFaulted => resetState == ResetState.Faulted;
    private bool IsResetBusy => resetState != ResetState.Idle;

    private bool TryStartResetPulse(PlcHmiInteractable control)
    {
        if (!CanReset() || !TryQueueD101(
                ResetPulseBit,
                ResetPulseBit,
                false,
                out resetRequestId))
        {
            return false;
        }

        resetControl = control;
        resetFaultLogged = false;
        resetFaultReason = string.Empty;
        resetRequestOnObserved = false;
        resetPulseMayBeOn = false;
        resetState = ResetState.SendingResetPulseOn;
        resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
        control.PlayMomentaryPress();
        control.SetPending(true);
        return true;
    }

    private bool CanReset()
    {
        if (resetState != ResetState.Idle || plcConnection == null ||
            !plcConnection.Connected || !plcConnection.D0Ready ||
            !plcConnection.D101Ready || !IsCommunicationFresh() ||
            !IsFreshDeviceOn("M400") ||
            !TryGetConfirmedEmergencyFeedback(out bool emergencyActive) ||
            emergencyActive || (plcConnection.D0Word & ResetAckBit) != 0 ||
            (plcConnection.D101Word & ResetPulseBit) != 0)
        {
            return false;
        }

        return TryFreshDevice("D100", out int d100) &&
               (d100 & ResetRequestBit) == 0;
    }

    private void UpdateResetStateMachine()
    {
        if (plcConnection == null)
        {
            return;
        }

        if (resetState == ResetState.Idle)
        {
            if (plcConnection.Connected && plcConnection.D101Ready &&
                (plcConnection.D101Word & ResetPulseBit) != 0)
            {
                BeginResetPulseOffRecovery();
                return;
            }

            if (TryGetResetRequest(out bool resetRequested))
            {
                bool ackOn = plcConnection.D0Ready &&
                             (plcConnection.D0Word & ResetAckBit) != 0;
                if (resetRequested && ackOn)
                {
                    resetRequestOnObserved = true;
                    if (processAdapter != null && !processAdapter.SafetyResetActive)
                    {
                        processAdapter.BeginSafetyReset();
                    }
                    processAdapter?.AllowSafetyProductSensorUpdates();
                    resetState = ResetState.WaitingForPlcRequestOff;
                }
                else if (resetRequested)
                {
                    BeginAcceptedPlcResetRequest();
                }
                else if (ackOn)
                {
                    if (processAdapter != null && !processAdapter.SafetyResetActive)
                    {
                        processAdapter.BeginSafetyReset();
                    }
                    BeginResetAckClear();
                }
            }
            return;
        }

        if (resetState == ResetState.Faulted)
        {
            UpdateFaultRecovery();
            return;
        }

        if (!plcConnection.Connected || !plcConnection.D0Ready ||
            !plcConnection.D101Ready)
        {
            EnterResetFault("PLC disconnected or D0/D101 is not ready");
            return;
        }

        if (resetRequestOnObserved &&
            resetState != ResetState.ClearingResetAck &&
            resetState != ResetState.VerifyingResetComplete &&
            !TryGetResetRequest(out _))
        {
            EnterResetFault("D100.5 data became stale during reset recovery");
            return;
        }

        if (resetRequestOnObserved &&
            (!TryGetConfirmedEmergencyFeedback(out bool resetEmergencyActive) ||
             resetEmergencyActive))
        {
            EnterResetFault("E-STOP became active or stale during reset recovery");
            return;
        }

        switch (resetState)
        {
            case ResetState.SendingResetPulseOn:
                if (TryTakeD101Write(resetRequestId, out WriteResult pulseOn))
                {
                    if (pulseOn.Result != 0)
                    {
                        EnterResetFault("D101.2 ON write failed");
                        return;
                    }

                    resetPulseMayBeOn = true;
                    resetPulseReleaseAt = Time.realtimeSinceStartup + resetPulseSeconds;
                    resetState = ResetState.HoldingResetPulse;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("D101.2 ON response timeout");
                }
                break;

            case ResetState.HoldingResetPulse:
                if (Time.realtimeSinceStartup >= resetPulseReleaseAt)
                {
                    BeginResetPulseOff(false);
                }
                break;

            case ResetState.SendingResetPulseOff:
                if (TryTakeD101Write(resetRequestId, out WriteResult pulseOff))
                {
                    if (pulseOff.Result != 0)
                    {
                        EnterResetFault("D101.2 OFF write failed");
                        return;
                    }

                    resetPulseMayBeOn = false;
                    resetState = ResetState.WaitingForPlcResetRequest;
                    resetStateDeadline = Time.realtimeSinceStartup + resetRequestTimeoutSeconds;
                    if (resetControl != null)
                    {
                        resetControl.SetPending(true);
                    }
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("D101.2 OFF response timeout");
                }
                break;

            case ResetState.WaitingForPlcResetRequest:
                if (!TryGetResetRequest(out bool waitingRequest))
                {
                    if (HasResetStateTimedOut())
                    {
                        EnterResetFault("D100.5 data became stale");
                    }
                }
                else if (waitingRequest)
                {
                    BeginAcceptedPlcResetRequest();
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("PLC did not assert D100.5");
                }
                break;

            case ResetState.QuiescingProcess:
                if (processAdapter == null)
                {
                    EnterResetFault("PlcProcessAdapter reference is missing");
                    return;
                }

                processAdapter.BeginSafetyReset();
                resetState = ResetState.WaitingForExistingD0Writes;
                resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
                break;

            case ResetState.WaitingForExistingD0Writes:
                if (!plcConnection.HasPendingD0Writes)
                {
                    processAdapter.RequestSafetyCylinderRetract();
                    resetState = ResetState.RetractingCylinder;
                    resetStateDeadline = Time.realtimeSinceStartup + cylinderRetractTimeoutSeconds;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("Existing D0 write barrier timeout");
                }
                break;

            case ResetState.RetractingCylinder:
                if (!TryGetConfirmedEmergencyFeedback(out bool emergencyActive) || emergencyActive)
                {
                    EnterResetFault("E-STOP became active or stale during cylinder retraction");
                }
                else if (processAdapter.IsCylinderRetracted)
                {
                    resetState = ResetState.RestoringProduct;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("Cylinder retraction timeout");
                }
                break;

            case ResetState.RestoringProduct:
                if (!processAdapter.RestoreSafetyProductToStart())
                {
                    EnterResetFault("Process product could not be restored to the start pose");
                    return;
                }

                if (!TryQueueTrackedD0(ProcessD0Mask, 0, ResetState.ClearingProcessBits))
                {
                    EnterResetFault("D0.1-D0.7 clear request was rejected");
                }
                break;

            case ResetState.ClearingProcessBits:
                if (TryTakeD0Write(resetRequestId, out WriteResult clearResult))
                {
                    if (clearResult.Result != 0 ||
                        (clearResult.Value & ProcessD0Mask) != 0)
                    {
                        EnterResetFault("D0.1-D0.7 clear write failed");
                        return;
                    }

                    resetState = ResetState.VerifyingSafeState;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("D0.1-D0.7 clear response timeout");
                }
                break;

            case ResetState.VerifyingSafeState:
                if (!VerifyPreAckSafeState())
                {
                    EnterResetFault("Unity safe state verification failed");
                    return;
                }

                if (!TryQueueTrackedD0(ResetAckBit, ResetAckBit, ResetState.SettingResetAck))
                {
                    EnterResetFault("D0.8 ON request was rejected");
                }
                break;

            case ResetState.SettingResetAck:
                if (TryTakeD0Write(resetRequestId, out WriteResult ackOn))
                {
                    if (ackOn.Result != 0 || (ackOn.Value & ResetAckBit) == 0)
                    {
                        EnterResetFault("D0.8 ON write failed");
                        return;
                    }

                    processAdapter.AllowSafetyProductSensorUpdates();
                    resetState = ResetState.WaitingForPlcRequestOff;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("D0.8 ON response timeout");
                }
                break;

            case ResetState.WaitingForPlcRequestOff:
                if (!TryGetResetRequest(out bool requestStillOn))
                {
                    EnterResetFault("D100.5 data became stale while ACK was ON");
                }
                else if (!requestStillOn)
                {
                    if (!plcConnection.HasPendingD0Writes &&
                        (processAdapter == null || !processAdapter.HasPendingProcessWrites))
                    {
                        BeginResetAckClear();
                    }
                }
                break;

            case ResetState.ClearingResetAck:
                if (TryTakeD0Write(resetRequestId, out WriteResult ackOff))
                {
                    if (ackOff.Result != 0 || (ackOff.Value & ResetAckBit) != 0)
                    {
                        EnterResetFault("D0.8 OFF write failed");
                        return;
                    }

                    resetState = ResetState.VerifyingResetComplete;
                    resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("D0.8 OFF response timeout");
                }
                break;

            case ResetState.VerifyingResetComplete:
                if (VerifyResetComplete())
                {
                    processAdapter.CompleteSafetyReset();
                    resetState = ResetState.Idle;
                    resetRequestOnObserved = false;
                    resetFaultLogged = false;
                    resetFaultReason = string.Empty;
                    if (resetControl != null)
                    {
                        resetControl.SetPending(false);
                        resetControl.SetFaulted(false);
                    }
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("Final PLC/Unity reset state verification timeout");
                }
                break;

            case ResetState.RecoveringResetPulseOff:
                if (TryTakeD101Write(resetRequestId, out WriteResult recoveredOff))
                {
                    if (recoveredOff.Result != 0)
                    {
                        EnterResetFault("Recovered D101.2 OFF write failed");
                        return;
                    }

                    resetPulseMayBeOn = false;
                    resetState = ResetState.Faulted;
                    UpdateFaultRecovery();
                }
                else if (HasResetStateTimedOut())
                {
                    EnterResetFault("Recovered D101.2 OFF response timeout");
                }
                break;
        }
    }

    private void BeginAcceptedPlcResetRequest()
    {
        if (resetRequestOnObserved)
        {
            return;
        }

        resetRequestOnObserved = true;
        resetState = ResetState.QuiescingProcess;
        resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
        if (resetControl != null)
        {
            resetControl.SetPending(true);
        }
    }

    private void BeginResetPulseOff(bool recovery)
    {
        if (!TryQueueD101(ResetPulseBit, 0, true, out resetRequestId))
        {
            EnterResetFault("D101.2 OFF request was rejected");
            return;
        }

        resetState = recovery
            ? ResetState.RecoveringResetPulseOff
            : ResetState.SendingResetPulseOff;
        resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
    }

    private void BeginResetPulseOffRecovery()
    {
        resetPulseMayBeOn = true;
        resetFaultReason = string.IsNullOrEmpty(resetFaultReason)
            ? "Recovered a latched D101.2 reset pulse"
            : resetFaultReason;
        BeginResetPulseOff(true);
    }

    private void BeginResetAckClear()
    {
        if (!TryQueueTrackedD0(ResetAckBit, 0, ResetState.ClearingResetAck))
        {
            EnterResetFault("D0.8 OFF request was rejected");
        }
    }

    private bool TryQueueTrackedD0(int mask, int enabledBits, ResetState nextState)
    {
        if (plcConnection == null || plcConnection.HasPendingD0Writes ||
            !plcConnection.TrySetD0MaskedBitsTracked(mask, enabledBits, out resetRequestId))
        {
            return false;
        }

        resetState = nextState;
        trackedResetD0Requests.Add(resetRequestId);
        resetStateDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
        return true;
    }

    private bool VerifyPreAckSafeState()
    {
        return plcConnection.Connected && plcConnection.D0Ready &&
               !plcConnection.HasPendingD0Writes &&
               (plcConnection.D0Word & ProcessD0Mask) == 0 &&
               processAdapter != null && processAdapter.VerifySafetyResetState() &&
               !processAdapter.HasPendingProcessWrites;
    }

    private bool VerifyResetComplete()
    {
        if (!TryGetResetRequest(out bool requestOn) || requestOn ||
            !TryFreshDevice("M30", out int run) || run != 0 ||
            !TryFreshDevice("D500", out int step) || step != 0)
        {
            return false;
        }

        return plcConnection.Connected && plcConnection.D0Ready &&
               plcConnection.D101Ready && !plcConnection.HasPendingD0Writes &&
               (plcConnection.D0Word & (ProcessD0Mask | ResetAckBit)) == 0 &&
               (plcConnection.D101Word & ResetPulseBit) == 0 &&
               processAdapter != null && processAdapter.VerifySafetyResetState() &&
               !processAdapter.HasPendingProcessWrites;
    }

    private void EnterResetFault(string reason)
    {
        resetState = ResetState.Faulted;
        resetFaultReason = reason;
        if (processAdapter != null)
        {
            processAdapter.FaultSafetyReset();
        }

        if (resetControl != null)
        {
            resetControl.SetPending(false);
            resetControl.SetFaulted(true);
        }

        if (!resetFaultLogged)
        {
            resetFaultLogged = true;
            Debug.LogError($"[PLC RESET] {reason}");
        }
    }

    private void UpdateFaultRecovery()
    {
        if (!plcConnection.Connected || !plcConnection.D0Ready ||
            !plcConnection.D101Ready || !IsCommunicationFresh())
        {
            return;
        }

        if ((plcConnection.D101Word & ResetPulseBit) != 0)
        {
            BeginResetPulseOffRecovery();
            return;
        }

        if (!TryGetResetRequest(out bool requestOn))
        {
            return;
        }

        bool ackOn = (plcConnection.D0Word & ResetAckBit) != 0;
        if (requestOn && ackOn)
        {
            resetRequestOnObserved = true;
            resetState = ResetState.WaitingForPlcRequestOff;
        }
        else if (requestOn)
        {
            resetRequestOnObserved = false;
            BeginAcceptedPlcResetRequest();
        }
        else if (ackOn)
        {
            BeginResetAckClear();
        }
        else if (processAdapter != null && processAdapter.VerifySafetyResetState() &&
                 TryFreshDevice("M30", out int run) && run == 0 &&
                 TryFreshDevice("D500", out int step) && step == 0)
        {
            processAdapter.CompleteSafetyReset();
            resetState = ResetState.Idle;
            resetFaultLogged = false;
            resetFaultReason = string.Empty;
            if (resetControl != null)
            {
                resetControl.SetFaulted(false);
            }
        }
    }

    private bool TryGetResetRequest(out bool active)
    {
        active = false;
        if (!TryFreshDevice("D100", out int d100))
        {
            return false;
        }

        active = (d100 & ResetRequestBit) != 0;
        return true;
    }

    private bool HasResetStateTimedOut()
    {
        return Time.realtimeSinceStartup >= resetStateDeadline;
    }

    private bool TryTakeD101Write(long requestId, out WriteResult result)
    {
        return TakeWriteResult(requestId, out result);
    }

    private bool TryTakeD0Write(long requestId, out WriteResult result)
    {
        if (completedD0Writes.TryGetValue(requestId, out result))
        {
            completedD0Writes.Remove(requestId);
            trackedResetD0Requests.Remove(requestId);
            return true;
        }

        return false;
    }

    private void RequestEarlyResetPulseRelease()
    {
        if (resetState == ResetState.HoldingResetPulse)
        {
            resetPulseReleaseAt = Time.realtimeSinceStartup;
        }
    }

    private void ForceResetPulseOffBestEffort()
    {
        if (plcConnection == null || !plcConnection.Connected ||
            !plcConnection.D101Ready ||
            (!resetPulseMayBeOn && (plcConnection.D101Word & ResetPulseBit) == 0))
        {
            return;
        }

        plcConnection.TrySetD101MaskedBits(ResetPulseBit, 0, true, out _);
    }

    private IEnumerator MaintainedWrite(
        int mask,
        int enabledBits,
        bool highPriority,
        Action<bool> completed)
    {
        if (!TryQueueD101(mask, enabledBits, highPriority, out long requestId))
        {
            completed(false);
            yield break;
        }

        yield return WaitForWrite(requestId);
        bool success = TakeWriteResult(requestId, out WriteResult writeResult) && writeResult.Result == 0;
        completed(success);
    }

    private IEnumerator ModeWrite(PlcHmiInteractable control, int enabledBits)
    {
        if (!TryQueueD101(ManualAutoMask, enabledBits, false, out long requestId))
        {
            CompleteModeWrite(control, false);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
        while (!completedWrites.ContainsKey(requestId) &&
               plcConnection != null && plcConnection.Connected &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        bool hasResult = TakeWriteResult(requestId, out WriteResult writeResult);
        if (!hasResult)
        {
            abandonedWriteRequests.Add(requestId);
        }

        bool success = hasResult && writeResult.Result == 0;
        CompleteModeWrite(control, success);
    }

    private void CompleteModeWrite(PlcHmiInteractable control, bool success)
    {
        modeWritePending = false;
        requestedSelectorState = PlcHmiInteractable.SelectorState.Unknown;
        control.SetPending(false);
        if (!success)
        {
            control.SetSelectorState(lastConfirmedSelectorState);
        }
    }

    private IEnumerator EmergencyWrite(PlcHmiInteractable control, bool targetActive)
    {
        int enabledBits = targetActive ? EmergencyStopBit : 0;
        if (!TryQueueD101(EmergencyStopBit, enabledBits, true, out long requestId))
        {
            CompleteEmergencyWrite(control, false, targetActive);
            yield break;
        }

        float writeDeadline = Time.realtimeSinceStartup + writeResponseTimeoutSeconds;
        while (!completedWrites.ContainsKey(requestId) &&
               plcConnection != null && plcConnection.Connected &&
               Time.realtimeSinceStartup < writeDeadline)
        {
            yield return null;
        }

        bool hasResult = TakeWriteResult(requestId, out WriteResult writeResult);
        if (!hasResult)
        {
            abandonedWriteRequests.Add(requestId);
        }

        if (!hasResult || writeResult.Result != 0)
        {
            CompleteEmergencyWrite(control, false, targetActive);
            yield break;
        }

        float feedbackDeadline = Time.realtimeSinceStartup + emergencyFeedbackTimeoutSeconds;
        bool feedbackConfirmed = false;
        while (plcConnection != null && plcConnection.Connected &&
               Time.realtimeSinceStartup < feedbackDeadline)
        {
            if (TryGetConfirmedEmergencyFeedback(out bool active) && active == targetActive)
            {
                feedbackConfirmed = true;
                break;
            }

            yield return null;
        }

        if (feedbackConfirmed && !targetActive)
        {
            while (control != null && control.IsAnimating)
            {
                yield return null;
            }
        }

        CompleteEmergencyWrite(control, feedbackConfirmed, targetActive);
    }

    private void CompleteEmergencyWrite(
        PlcHmiInteractable control,
        bool success,
        bool targetActive)
    {
        emergencyWritePending = false;
        requestedEmergencyActive = false;
        control.SetPending(false);
        if (success)
        {
            lastConfirmedEmergencyActive = targetActive;
            hasConfirmedEmergencyState = true;
            control.SetLatched(targetActive);
        }
        else
        {
            control.SetLatched(hasConfirmedEmergencyState && lastConfirmedEmergencyActive);
        }
    }

    private bool TryQueueD101(int mask, int enabledBits, bool highPriority, out long requestId)
    {
        requestId = 0;
        return plcConnection != null && plcConnection.TrySetD101MaskedBits(
            mask, enabledBits, highPriority, out requestId);
    }

    private IEnumerator WaitForWrite(long requestId)
    {
        while (!completedWrites.ContainsKey(requestId) &&
               plcConnection != null && plcConnection.Connected)
        {
            yield return null;
        }
    }

    private bool TakeWriteResult(long requestId, out WriteResult result)
    {
        if (completedWrites.TryGetValue(requestId, out result))
        {
            completedWrites.Remove(requestId);
            return true;
        }

        return false;
    }

    private void OnD101WriteCompleted(long requestId, int result, int value)
    {
        if (abandonedWriteRequests.Remove(requestId))
        {
            return;
        }

        completedWrites[requestId] = new WriteResult { Result = result, Value = value };
    }

    private void OnD0WriteRequestCompleted(long requestId, int result, int value)
    {
        if (trackedResetD0Requests.Contains(requestId))
        {
            completedD0Writes[requestId] = new WriteResult { Result = result, Value = value };
        }
    }

    private void SetPulsePending(bool isStart, bool value)
    {
        if (isStart)
        {
            startPulsePending = value;
        }
        else
        {
            stopPulsePending = value;
        }
    }

    private void CachePanelParts()
    {
        controls = GetComponentsInChildren<PlcHmiInteractable>(true);
        indicators = GetComponentsInChildren<PlcHmiIndicator>(true);
        foreach (Text text in GetComponentsInChildren<Text>(true))
        {
            switch (text.gameObject.name)
            {
                case "StepValue": monitorStep = text; break;
                case "StepStatusValue": monitorStepStatus = text; break;
                case "TotalValue": monitorTotal = text; break;
                case "OkValue": monitorOk = text; break;
                case "NgValue": monitorNg = text; break;
                case "AlarmCodeValue": monitorAlarmCode = text; break;
                case "AlarmStatusValue": monitorAlarmStatus = text; break;
            }
        }

        monitorAlarmPanel = null;
        foreach (Image image in GetComponentsInChildren<Image>(true))
        {
            if (image.gameObject.name == "AlarmPanel")
            {
                monitorAlarmPanel = image;
                break;
            }
        }
    }

    private void RefreshPanel()
    {
        bool connected = plcConnection != null && plcConnection.Connected;
        SetIndicator(PlcHmiIndicator.IndicatorKind.PlcConnected,
            !connected ? PlcHmiIndicator.LampState.Off : IsCommunicationFresh()
                ? PlcHmiIndicator.LampState.On
                : PlcHmiIndicator.LampState.Stale);
        SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind.CommMode, "M400");
        SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind.Run, "M30");
        SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind.Auto, "M20");
        SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind.Manual, "M21");
        SetIndicatorFromWordBit(PlcHmiIndicator.IndicatorKind.InspectionRequest, "D100", 1 << 1);
        SetIndicatorFromWordBit(PlcHmiIndicator.IndicatorKind.InspectionOk, "D0", 1 << 3);
        SetIndicatorFromWordBit(PlcHmiIndicator.IndicatorKind.InspectionNg, "D0", 1 << 4);
        SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind.Alarm, "M200");

        PlcHmiInteractable.SelectorState confirmedSelectorState = GetConfirmedD101SelectorState();
        if (confirmedSelectorState != PlcHmiInteractable.SelectorState.Unknown)
        {
            lastConfirmedSelectorState = confirmedSelectorState;
        }
        if (TryGetConfirmedEmergencyFeedback(out bool confirmedEmergencyActive))
        {
            lastConfirmedEmergencyActive = confirmedEmergencyActive;
            hasConfirmedEmergencyState = true;
        }

        foreach (PlcHmiInteractable control in controls)
        {
            if (control.Kind == PlcHmiInteractable.ControlKind.ModeSelector)
            {
                control.SetSelectorState(modeWritePending
                    ? requestedSelectorState
                    : lastConfirmedSelectorState);
            }
            else if (control.Kind == PlcHmiInteractable.ControlKind.EmergencyStop)
            {
                if (!emergencyWritePending)
                {
                    control.SetLatched(
                        hasConfirmedEmergencyState && lastConfirmedEmergencyActive);
                }
            }


            control.SetAvailable(IsControlAvailable(control.Kind));
            if (control.Kind == PlcHmiInteractable.ControlKind.ModeSelector)
            {
                control.SetPending(modeWritePending);
            }
            else if (control.Kind == PlcHmiInteractable.ControlKind.EmergencyStop)
            {
                control.SetPending(emergencyWritePending);
            }
            else if (control.Kind == PlcHmiInteractable.ControlKind.Reset)
            {
                resetControl = control;
                control.SetPending(IsResetBusy && resetState != ResetState.Faulted);
                control.SetFaulted(resetState == ResetState.Faulted);
            }
        }

        RefreshInformationDisplay();
    }

    private bool IsControlAvailable(PlcHmiInteractable.ControlKind kind)
    {
        if (IsResetBusy &&
            kind != PlcHmiInteractable.ControlKind.EmergencyStop &&
            kind != PlcHmiInteractable.ControlKind.Reset)
        {
            return false;
        }

        bool ready = plcConnection != null && plcConnection.Connected && plcConnection.D101Ready;
        switch (kind)
        {
            case PlcHmiInteractable.ControlKind.Start: return CanStart() && !startPulsePending;
            case PlcHmiInteractable.ControlKind.Stop:
                return ready && IsCommunicationFresh() && !stopPulsePending;
            case PlcHmiInteractable.ControlKind.ModeSelector:
                return ready && IsCommunicationFresh() && IsFreshDeviceOn("M400") && !modeWritePending;
            case PlcHmiInteractable.ControlKind.InspectionOk:
            case PlcHmiInteractable.ControlKind.InspectionNg:
                return IsCommunicationFresh() && processAdapter != null &&
                       processAdapter.CanSubmitInspectionResult;
            case PlcHmiInteractable.ControlKind.EmergencyStop:
                return ready && IsCommunicationFresh() && IsFreshDeviceOn("M400") && !emergencyWritePending;
            case PlcHmiInteractable.ControlKind.EmergencyRelease:
                return false;
            case PlcHmiInteractable.ControlKind.Reset:
                return CanReset();
            default: return false;
        }
    }

    private bool CanStart()
    {
        if (IsResetBusy || plcConnection == null || !plcConnection.Connected || !plcConnection.D101Ready ||
            !IsCommunicationFresh() || !IsFreshDeviceOn("M400") || GetConfirmedMode() != ConfirmedMode.Auto ||
            !TryFreshDevice("M200", out int alarm) || alarm != 0)
        {
            return false;
        }

        bool emergencyActive = IsEmergencyStopActive(out bool known);
        return known && !emergencyActive;
    }

    private void SetIndicatorFromDevice(PlcHmiIndicator.IndicatorKind kind, string device)
    {
        if (plcConnection == null || !plcConnection.Connected ||
            !plcConnection.TryGetCachedDevice(device, out int value, out float age) ||
            age > staleAfterSeconds)
        {
            SetIndicator(kind, plcConnection != null && plcConnection.Connected
                ? PlcHmiIndicator.LampState.Stale
                : PlcHmiIndicator.LampState.Off);
            return;
        }

        SetIndicator(kind, value != 0 ? PlcHmiIndicator.LampState.On : PlcHmiIndicator.LampState.Off);
    }

    private void SetIndicatorFromWordBit(
        PlcHmiIndicator.IndicatorKind kind,
        string device,
        int bit)
    {
        if (plcConnection == null || !plcConnection.Connected ||
            !plcConnection.TryGetCachedDevice(device, out int value, out float age) ||
            age > staleAfterSeconds)
        {
            SetIndicator(kind, plcConnection != null && plcConnection.Connected
                ? PlcHmiIndicator.LampState.Stale
                : PlcHmiIndicator.LampState.Off);
            return;
        }

        SetIndicator(kind, (value & bit) != 0
            ? PlcHmiIndicator.LampState.On
            : PlcHmiIndicator.LampState.Off);
    }

    private void SetIndicator(PlcHmiIndicator.IndicatorKind kind, PlcHmiIndicator.LampState state)
    {
        foreach (PlcHmiIndicator indicator in indicators)
        {
            if (indicator.Kind == kind)
            {
                indicator.SetState(state);
            }
        }
    }

    private void SetMonitorValue(Text target, string device, string prefix)
    {
        if (target == null)
        {
            return;
        }

        if (plcConnection != null && plcConnection.Connected &&
            plcConnection.TryGetCachedDevice(device, out int value, out float age) &&
            age <= staleAfterSeconds)
        {
            target.text = prefix + value;
            target.color = new Color(0.45f, 1f, 0.65f);
        }
        else
        {
            target.text = prefix + "STALE";
            target.color = new Color(1f, 0.75f, 0.25f);
        }
    }

    private void RefreshInformationDisplay()
    {
        if (TryFreshDevice("D500", out int step))
        {
            SetText(monitorStep, step.ToString("D3"), new Color(0.1f, 0.85f, 1f));
            SetText(monitorStepStatus, GetStepStatus(step), Color.white);
        }
        else
        {
            SetText(monitorStep, "---", new Color(1f, 0.7f, 0.2f));
            SetText(monitorStepStatus, "COMM LOST", new Color(1f, 0.7f, 0.2f));
        }

        SetCountText(monitorTotal, "D10", Color.white);
        SetCountText(monitorOk, "D11", new Color(0.2f, 1f, 0.35f));
        SetCountText(monitorNg, "D12", new Color(1f, 0.2f, 0.12f));

        bool alarmFresh = TryFreshDevice("M200", out int alarmState);
        bool codeFresh = TryFreshDevice("D600", out int alarmCode);
        if (!alarmFresh || !codeFresh)
        {
            SetText(monitorAlarmCode, "---", new Color(1f, 0.7f, 0.2f));
            SetText(monitorAlarmStatus, "COMM LOST", new Color(1f, 0.7f, 0.2f));
            SetAlarmPanelColor(new Color(0.28f, 0.18f, 0.03f, 0.96f));
        }
        else if (alarmState == 0 && alarmCode == 0)
        {
            SetText(monitorAlarmCode, "000", Color.white);
            SetText(monitorAlarmStatus, "NORMAL", new Color(0.2f, 1f, 0.35f));
            SetAlarmPanelColor(new Color(0.025f, 0.075f, 0.055f, 0.96f));
        }
        else
        {
            SetText(monitorAlarmCode, alarmCode.ToString("D3"), new Color(1f, 0.16f, 0.08f));
            SetText(monitorAlarmStatus, GetAlarmStatus(alarmCode), new Color(1f, 0.3f, 0.18f));
            SetAlarmPanelColor(new Color(0.28f, 0.025f, 0.02f, 0.97f));
        }

        if (resetState == ResetState.Faulted)
        {
            SetText(monitorStepStatus, "RESET FAULT", new Color(1f, 0.15f, 0.08f));
            SetText(monitorAlarmStatus, "RESET: " + resetFaultReason, new Color(1f, 0.2f, 0.1f));
            SetAlarmPanelColor(new Color(0.35f, 0.015f, 0.01f, 0.98f));
        }
        else if (IsResetBusy)
        {
            SetText(monitorStepStatus, "RESET " + GetResetStatusLabel(), new Color(1f, 0.72f, 0.08f));
        }
    }

    private string GetResetStatusLabel()
    {
        switch (resetState)
        {
            case ResetState.SendingResetPulseOn:
            case ResetState.HoldingResetPulse:
            case ResetState.SendingResetPulseOff:
                return "PB";
            case ResetState.WaitingForPlcResetRequest:
                return "PLC WAIT";
            case ResetState.RetractingCylinder:
                return "CYLINDER";
            case ResetState.RestoringProduct:
                return "PRODUCT";
            case ResetState.ClearingProcessBits:
                return "CLEAR";
            case ResetState.SettingResetAck:
                return "ACK ON";
            case ResetState.WaitingForPlcRequestOff:
                return "WAIT OFF";
            case ResetState.ClearingResetAck:
                return "ACK OFF";
            default:
                return "SAFE";
        }
    }

    private void SetCountText(Text target, string device, Color color)
    {
        bool fresh = TryFreshDevice(device, out int value);
        SetText(target,
            fresh ? value.ToString("D5") : "---",
            fresh ? color : new Color(1f, 0.7f, 0.2f));
    }

    private static void SetText(Text target, string value, Color color)
    {
        if (target == null)
        {
            return;
        }

        target.text = value;
        target.color = color;
    }

    private void SetAlarmPanelColor(Color color)
    {
        if (monitorAlarmPanel != null)
        {
            monitorAlarmPanel.color = color;
        }
    }

    private static string GetStepStatus(int step)
    {
        switch (step)
        {
            case 100: return "READY";
            case 110: return "CONVEYING";
            case 120: return "INSPECTION";
            case 130: return "RESULT CHECK";
            case 200: return "OK DISCHARGE";
            case 300: return "CYLINDER EXTEND";
            case 310: return "NG DISCHARGE";
            case 320: return "CYLINDER RETRACT";
            case 400: return "CYCLE COMPLETE";
            default: return "UNKNOWN";
        }
    }

    private static string GetAlarmStatus(int alarmCode)
    {
        switch (alarmCode)
        {
            case 102: return "INSPECTION POSITION TIMEOUT";
            case 103: return "INSPECTION COMPLETE TIMEOUT";
            case 104: return "OK/NG SIGNAL CONFLICT";
            case 201: return "CYLINDER EXTEND TIMEOUT";
            case 202: return "CYLINDER RETRACT TIMEOUT";
            case 203: return "CYLINDER SENSOR CONFLICT";
            case 901: return "EMERGENCY STOP";
            default: return "UNKNOWN ALARM";
        }
    }

    private bool IsCommunicationFresh()
    {
        return plcConnection != null && plcConnection.Connected &&
               plcConnection.LastDataUpdateRealtime >= 0f &&
               Time.realtimeSinceStartup - plcConnection.LastDataUpdateRealtime <= staleAfterSeconds;
    }

    private bool IsFreshDeviceOn(string device)
    {
        return TryFreshDevice(device, out int value) && value != 0;
    }

    private ConfirmedMode GetConfirmedMode()
    {
        if (TryFreshDevice("M20", out int auto) && TryFreshDevice("M21", out int manual))
        {
            if (auto != 0 && manual == 0) return ConfirmedMode.Auto;
            if (manual != 0 && auto == 0) return ConfirmedMode.Manual;
            if (manual == 0 && auto == 0) return ConfirmedMode.Off;
            return ConfirmedMode.Unknown;
        }

        if (plcConnection != null && plcConnection.Connected && plcConnection.D101Ready)
        {
            int bits = plcConnection.D101Word & ManualAutoMask;
            if (bits == ManualBit) return ConfirmedMode.Manual;
            if (bits == AutoBit) return ConfirmedMode.Auto;
            if (bits == 0) return ConfirmedMode.Off;
        }

        return ConfirmedMode.Unknown;
    }

    private PlcHmiInteractable.SelectorState GetConfirmedD101SelectorState()
    {
        if (plcConnection == null || !plcConnection.Connected || !plcConnection.D101Ready)
        {
            return PlcHmiInteractable.SelectorState.Unknown;
        }

        int bits = plcConnection.D101Word & ManualAutoMask;
        if (bits == AutoBit) return PlcHmiInteractable.SelectorState.Auto;
        if (bits == ManualBit) return PlcHmiInteractable.SelectorState.Manual;
        if (bits == 0) return PlcHmiInteractable.SelectorState.Off;
        return PlcHmiInteractable.SelectorState.Unknown;
    }

    private static PlcHmiInteractable.SelectorState GetNextSelectorState(
        PlcHmiInteractable.SelectorState current)
    {
        switch (current)
        {
            case PlcHmiInteractable.SelectorState.Off:
                return PlcHmiInteractable.SelectorState.Auto;
            case PlcHmiInteractable.SelectorState.Auto:
                return PlcHmiInteractable.SelectorState.Manual;
            case PlcHmiInteractable.SelectorState.Manual:
                return PlcHmiInteractable.SelectorState.Off;
            default:
                return PlcHmiInteractable.SelectorState.Unknown;
        }
    }

    private bool IsEmergencyStopActive(out bool known)
    {
        if (TryFreshDevice("M13", out int m13))
        {
            known = true;
            return m13 != 0;
        }

        known = plcConnection != null && plcConnection.Connected && plcConnection.D101Ready;
        return known && (plcConnection.D101Word & EmergencyStopBit) != 0;
    }

    private bool TryGetConfirmedEmergencyFeedback(out bool active)
    {
        active = false;
        if (!TryFreshDevice("M13", out int m13))
        {
            return false;
        }

        active = m13 != 0;
        return true;
    }

    private bool TryFreshDevice(string device, out int value)
    {
        value = 0;
        return plcConnection != null && plcConnection.Connected &&
               plcConnection.TryGetCachedDevice(device, out value, out float age) &&
               age <= staleAfterSeconds;
    }
}

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public sealed class PlcConnectionTest : MonoBehaviour
{
    private const float PollRequestIntervalSeconds = 0.1f;
    private const int RequestFailureCode = unchecked((int)0x80004005);

    private static readonly string[] PollSequence =
    {
        "D100", "M400", "D100", "M30", "D100", "M20",
        "D100", "M21", "D100", "M200", "D100", "D600", "D100", "D500",
        "D100", "D10", "D100", "D11", "D100", "D12",
        "D100", "D0", "D100", "D101", "D100", "M13"
    };

    private enum RequestKind
    {
        Read,
        Write
    }

    private sealed class PlcRequest
    {
        public long Id;
        public RequestKind Kind;
        public string Device;
        public int Value;
        public int Mask;
        public int EnabledBits;
        public bool IsD0MaskedWrite;
        public bool IsD101MaskedWrite;
    }

    private struct CachedDeviceValue
    {
        public int Value;
        public float UpdatedAt;
    }

    private static PlcConnectionTest activeInstance;

    [Header("PLC Status")]
    [SerializeField] private bool connected;
    [SerializeField] private int d100Value;
    [SerializeField] private int d0Word;
    [SerializeField] private int d101Word;
    [SerializeField] private int d101LastWritten;
    [SerializeField] private bool d0Ready;
    [SerializeField] private bool d101Ready;
    [SerializeField] private float lastDataUpdateRealtime = -1f;
    [SerializeField] private string lastOpenCode = "--------";
    [SerializeField] private string lastReadCode = "--------";
    [SerializeField] private string lastWriteCode = "--------";
    [SerializeField] private string lastBridgeDiagnostic = "--------";

    private readonly ConcurrentQueue<string> responseQueue = new ConcurrentQueue<string>();
    private readonly LinkedList<PlcRequest> requestQueue = new LinkedList<PlcRequest>();
    private readonly Dictionary<string, CachedDeviceValue> deviceCache =
        new Dictionary<string, CachedDeviceValue>(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim closeResponseReceived = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim bridgeProcessExited = new ManualResetEventSlim(false);

    private Process bridgeProcess;
    private Coroutine pollCoroutine;
    private PlcRequest inFlightRequest;
    private int shutdownStarted;
    private int pollIndex;
    private long nextRequestId;
    private string closeResponseLine;

    private bool d0WriteInProgress;
    private int d0InFlightValue;
    private bool d0Queued;
    private int d0QueuedValue;

    public bool Connected => connected;
    public int D100Value => d100Value;
    public int D0Word => d0Word;
    public bool D0Ready => d0Ready;
    public int D101Word => d101Word;
    public bool D101Ready => d101Ready;
    public int D101LastWritten => d101LastWritten;
    public float LastDataUpdateRealtime => lastDataUpdateRealtime;
    public string LastBridgeDiagnostic => lastBridgeDiagnostic;
    public int PendingRequestCount => requestQueue.Count + (inFlightRequest != null ? 1 : 0);
    public bool HasPendingD0Writes => HasPendingD0WriteRequest();

    public event Action<int, int> D0WriteCompleted;
    public event Action<long, int, int> D0WriteRequestCompleted;
    public event Action<long, int, int> D101WriteCompleted;

    private void Awake()
    {
        // Inspector fields are diagnostics only; readiness must be earned in this Play session.
        connected = false;
        d0Ready = false;
        d101Ready = false;
        lastDataUpdateRealtime = -1f;
        deviceCache.Clear();
        requestQueue.Clear();
        inFlightRequest = null;

        if (activeInstance != null && activeInstance != this)
        {
            Debug.LogError("[PLC TEST] Duplicate PlcConnectionTest component detected. The duplicate was disabled.");
            enabled = false;
            return;
        }

        activeInstance = this;
    }

    private void Start()
    {
        if (activeInstance == this)
        {
            StartBridge();
        }
    }

    private void Update()
    {
        while (responseQueue.TryDequeue(out string response))
        {
            HandleResponse(response);
        }

        if (connected && bridgeProcess != null && bridgeProcess.HasExited)
        {
            SetDisconnected($"Bridge exited unexpectedly. ExitCode = {bridgeProcess.ExitCode}");
        }
    }

    public static int CalculateMaskedWord(int currentWord, int mask, int enabledBits)
    {
        mask &= 0xFFFF;
        return ((currentWord & ~mask) | (enabledBits & mask)) & 0xFFFF;
    }

    public bool TryGetCachedDevice(string device, out int value, out float ageSeconds)
    {
        if (deviceCache.TryGetValue(device, out CachedDeviceValue cached))
        {
            value = cached.Value;
            ageSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - cached.UpdatedAt);
            return true;
        }

        value = 0;
        ageSeconds = float.PositiveInfinity;
        return false;
    }

    public bool IsDeviceStale(string device, float staleAfterSeconds)
    {
        return !TryGetCachedDevice(device, out _, out float ageSeconds) ||
               ageSeconds > Mathf.Max(0f, staleAfterSeconds);
    }

    public void WriteDevice(string device, int value)
    {
        if (!connected)
        {
            Debug.LogError($"[PLC TEST] Write rejected: not connected. Device = {device}");
            return;
        }

        if (device.Equals("D101", StringComparison.OrdinalIgnoreCase))
        {
            TrySetD101MaskedBits(0xFFFF, value, false, out _);
            return;
        }

        EnqueueWrite(device, value & 0xFFFF, false);
    }

    public bool SetD0Bit(int bitIndex, bool enabled)
    {
        if (bitIndex < 0 || bitIndex > 15)
        {
            return false;
        }

        int mask = 1 << bitIndex;
        return SetD0MaskedBits(mask, enabled ? mask : 0);
    }

    public bool SetD0MaskedBits(int mask, int enabledBits)
    {
        if (!connected || !d0Ready)
        {
            return false;
        }

        int baseValue = d0Queued
            ? d0QueuedValue
            : d0WriteInProgress
                ? d0InFlightValue
                : d0Word;
        int newValue = CalculateMaskedWord(baseValue, mask, enabledBits);
        if (newValue == baseValue)
        {
            return false;
        }

        Debug.Log($"[PLC INPUT] WRITE D0 requested: old={baseValue}, new={newValue}");
        if (d0WriteInProgress)
        {
            d0QueuedValue = newValue;
            d0Queued = true;
            return true;
        }

        d0Queued = false;
        StartD0Write(newValue);
        return true;
    }

    public bool TrySetD0MaskedBitsTracked(
        int mask,
        int enabledBits,
        out long requestId)
    {
        requestId = 0;
        mask &= 0xFFFF;
        if (!connected || !d0Ready || mask == 0)
        {
            return false;
        }

        var request = new PlcRequest
        {
            Id = ++nextRequestId,
            Kind = RequestKind.Write,
            Device = "D0",
            Mask = mask,
            EnabledBits = enabledBits & mask,
            IsD0MaskedWrite = true
        };

        requestId = request.Id;
        // RESET writes must stay behind every previously queued process write.
        EnqueueRequest(request, false);
        return true;
    }

    public bool SetD101MaskedBits(int mask, int enabledBits)
    {
        return TrySetD101MaskedBits(mask, enabledBits, false, out _);
    }

    public bool TrySetD101MaskedBits(
        int mask,
        int enabledBits,
        bool highPriority,
        out long requestId)
    {
        requestId = 0;
        mask &= 0xFFFF;
        if (!connected || !d101Ready || mask == 0)
        {
            return false;
        }

        var request = new PlcRequest
        {
            Id = ++nextRequestId,
            Kind = RequestKind.Write,
            Device = "D101",
            Mask = mask,
            EnabledBits = enabledBits & mask,
            IsD101MaskedWrite = true
        };

        requestId = request.Id;
        EnqueueRequest(request, highPriority);
        return true;
    }

    private void StartD0Write(int value)
    {
        d0WriteInProgress = true;
        d0InFlightValue = value & 0xFFFF;
        EnqueueWrite("D0", d0InFlightValue, true);
    }

    private void TryStartQueuedD0Write()
    {
        if (d0WriteInProgress || !d0Queued)
        {
            return;
        }

        int value = d0QueuedValue & 0xFFFF;
        d0Queued = false;
        if (value != d0Word)
        {
            StartD0Write(value);
        }
    }

    [ContextMenu("Write D101 = 5678")]
    public void WriteD101TestValue()
    {
        WriteDevice("D101", 5678);
    }

    private void StartBridge()
    {
        if (bridgeProcess != null && !bridgeProcess.HasExited)
        {
            Debug.LogWarning("[PLC TEST] Bridge is already running.");
            return;
        }

        string bridgePath = Path.Combine(Application.dataPath, "Plugins", "PlcMxBridge.exe");
        try
        {
            bridgeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = bridgePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            bridgeProcess.OutputDataReceived += OnBridgeOutput;
            bridgeProcess.ErrorDataReceived += OnBridgeError;
            bridgeProcess.Exited += OnBridgeExited;

            if (!bridgeProcess.Start())
            {
                throw new InvalidOperationException("PlcMxBridge.exe did not start.");
            }

            bridgeProcess.StandardInput.AutoFlush = true;
            bridgeProcess.BeginOutputReadLine();
            bridgeProcess.BeginErrorReadLine();
        }
        catch (Exception exception)
        {
            lastOpenCode = $"0x{exception.HResult:X8}";
            SetDisconnected($"Open exception. Code = {lastOpenCode}\n{exception}");
        }
    }

    private IEnumerator PollDevices()
    {
        var wait = new WaitForSecondsRealtime(PollRequestIntervalSeconds);
        while (connected && Volatile.Read(ref shutdownStarted) == 0)
        {
            string device = PollSequence[pollIndex];
            pollIndex = (pollIndex + 1) % PollSequence.Length;
            EnqueueRead(device, false);
            yield return wait;
        }
    }

    private void EnqueueRead(string device, bool highPriority)
    {
        if (!connected || HasPendingRead(device) ||
            (device.Equals("D0", StringComparison.OrdinalIgnoreCase) && d0WriteInProgress))
        {
            return;
        }

        EnqueueRequest(new PlcRequest
        {
            Id = ++nextRequestId,
            Kind = RequestKind.Read,
            Device = device
        }, highPriority);
    }

    private void EnqueueWrite(string device, int value, bool highPriority)
    {
        if (!connected)
        {
            return;
        }

        EnqueueRequest(new PlcRequest
        {
            Id = ++nextRequestId,
            Kind = RequestKind.Write,
            Device = device,
            Value = value & 0xFFFF
        }, highPriority);
    }

    private void EnqueueRequest(PlcRequest request, bool highPriority)
    {
        if (highPriority)
        {
            requestQueue.AddFirst(request);
        }
        else
        {
            requestQueue.AddLast(request);
        }

        TryDispatchNextRequest();
    }

    private bool HasPendingRead(string device)
    {
        if (inFlightRequest != null && inFlightRequest.Kind == RequestKind.Read &&
            inFlightRequest.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (PlcRequest request in requestQueue)
        {
            if (request.Kind == RequestKind.Read &&
                request.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasPendingD0WriteRequest()
    {
        if (d0WriteInProgress || d0Queued)
        {
            return true;
        }

        if (inFlightRequest != null && inFlightRequest.Kind == RequestKind.Write &&
            inFlightRequest.Device.Equals("D0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (PlcRequest request in requestQueue)
        {
            if (request.Kind == RequestKind.Write &&
                request.Device.Equals("D0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void TryDispatchNextRequest()
    {
        if (!connected || inFlightRequest != null || requestQueue.Count == 0)
        {
            return;
        }

        inFlightRequest = requestQueue.First.Value;
        requestQueue.RemoveFirst();

        if (inFlightRequest.IsD0MaskedWrite)
        {
            inFlightRequest.Value = CalculateMaskedWord(
                d0Word,
                inFlightRequest.Mask,
                inFlightRequest.EnabledBits);
        }
        else if (inFlightRequest.IsD101MaskedWrite)
        {
            inFlightRequest.Value = CalculateMaskedWord(
                d101Word,
                inFlightRequest.Mask,
                inFlightRequest.EnabledBits);
        }

        string command = inFlightRequest.Kind == RequestKind.Read
            ? $"READ {inFlightRequest.Device}"
            : $"WRITE {inFlightRequest.Device} {inFlightRequest.Value.ToString(CultureInfo.InvariantCulture)}";
        SendCommand(command);
    }

    private void OnBridgeOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            string response = eventArgs.Data.Trim();
            if (response.StartsWith("CLOSE ", StringComparison.Ordinal))
            {
                closeResponseLine = response;
                closeResponseReceived.Set();
            }

            responseQueue.Enqueue(response);
        }
    }

    private void OnBridgeError(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            responseQueue.Enqueue("IPC_ERROR " + eventArgs.Data.Trim());
        }
    }

    private void OnBridgeExited(object sender, EventArgs eventArgs)
    {
        bridgeProcessExited.Set();
        if (Volatile.Read(ref shutdownStarted) == 0)
        {
            responseQueue.Enqueue("BRIDGE_EXITED");
        }
    }

    private void HandleResponse(string response)
    {
        string[] parts = response.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0])
        {
            case "[BRIDGE]":
                lastBridgeDiagnostic = response;
                Debug.Log(response);
                break;
            case "OPEN":
                HandleOpen(parts);
                break;
            case "READ":
                HandleRead(parts);
                break;
            case "WRITE":
                HandleWrite(parts);
                break;
            case "CLOSE":
                connected = false;
                Debug.Log($"[PLC TEST] Close result. Code = {parts[1]}");
                break;
            case "ERROR":
                SetDisconnected($"Bridge error: {response}");
                break;
            case "IPC_ERROR":
                SetDisconnected($"Bridge stderr: {response.Substring("IPC_ERROR ".Length)}");
                break;
            case "BRIDGE_EXITED":
                SetDisconnected("Bridge process exited unexpectedly.");
                break;
            default:
                Debug.LogWarning($"[PLC TEST] Unknown bridge response: {response}");
                break;
        }
    }

    private void HandleOpen(string[] parts)
    {
        if (parts.Length < 2)
        {
            SetDisconnected("Malformed Open response.");
            return;
        }

        int result = ParseHex(parts[1]);
        lastOpenCode = $"0x{result:X8}";
        if (result != 0)
        {
            SetDisconnected($"Open failed. Code = {lastOpenCode}");
            return;
        }

        connected = true;
        Debug.Log($"[PLC TEST] Connected. Open Code = {lastOpenCode}");
        EnqueueRead("D0", true);
        EnqueueRead("D101", true);
        pollCoroutine = StartCoroutine(PollDevices());
    }

    private void HandleRead(string[] parts)
    {
        if (parts.Length < 4 || !ResponseMatches(RequestKind.Read, parts[1]))
        {
            SetDisconnected("Malformed or out-of-order Read response.");
            return;
        }

        int result = ParseHex(parts[2]);
        lastReadCode = $"0x{result:X8}";
        if (result != 0)
        {
            SetDisconnected($"Read failed. Device = {parts[1]}, Code = {lastReadCode}");
            return;
        }

        int newValue = int.Parse(parts[3], CultureInfo.InvariantCulture) & 0xFFFF;
        UpdateDeviceCache(parts[1], newValue);
        if (parts[1].Equals("D0", StringComparison.OrdinalIgnoreCase))
        {
            d0Word = newValue;
            d0Ready = true;
        }
        else if (parts[1].Equals("D100", StringComparison.OrdinalIgnoreCase))
        {
            d100Value = newValue;
        }
        else if (parts[1].Equals("D101", StringComparison.OrdinalIgnoreCase))
        {
            d101Word = newValue;
            d101Ready = true;
        }

        CompleteInFlightRequest();
    }

    private void HandleWrite(string[] parts)
    {
        if (parts.Length < 3 || !ResponseMatches(RequestKind.Write, parts[1]))
        {
            SetDisconnected("Malformed or out-of-order Write response.");
            return;
        }

        PlcRequest completedRequest = inFlightRequest;
        int result = ParseHex(parts[2]);
        lastWriteCode = $"0x{result:X8}";
        bool isD0Write = completedRequest.Device.Equals("D0", StringComparison.OrdinalIgnoreCase);
        bool isD101Write = completedRequest.Device.Equals("D101", StringComparison.OrdinalIgnoreCase);

        if (result != 0)
        {
            if (isD0Write)
            {
                D0WriteCompleted?.Invoke(result, completedRequest.Value);
                D0WriteRequestCompleted?.Invoke(
                    completedRequest.Id,
                    result,
                    completedRequest.Value);
                d0WriteInProgress = false;
                d0Queued = false;
            }
            else if (isD101Write)
            {
                D101WriteCompleted?.Invoke(completedRequest.Id, result, completedRequest.Value);
            }

            inFlightRequest = null;
            SetDisconnected($"Write failed. Device = {parts[1]}, Code = {lastWriteCode}");
            return;
        }

        UpdateDeviceCache(completedRequest.Device, completedRequest.Value);
        if (isD0Write)
        {
            d0Word = completedRequest.Value;
            d0WriteInProgress = false;
            D0WriteCompleted?.Invoke(result, d0Word);
            D0WriteRequestCompleted?.Invoke(
                completedRequest.Id,
                result,
                d0Word);
        }
        else if (isD101Write)
        {
            d101Word = completedRequest.Value;
            d101LastWritten = completedRequest.Value;
            d101Ready = true;
            D101WriteCompleted?.Invoke(completedRequest.Id, result, completedRequest.Value);
        }

        Debug.Log($"[PLC TEST] Write succeeded. Device = {parts[1]}, value={completedRequest.Value}, Code = {lastWriteCode}");
        CompleteInFlightRequest();
        if (isD0Write)
        {
            TryStartQueuedD0Write();
        }
    }

    private bool ResponseMatches(RequestKind kind, string device)
    {
        return inFlightRequest != null && inFlightRequest.Kind == kind &&
               inFlightRequest.Device.Equals(device, StringComparison.OrdinalIgnoreCase);
    }

    private void CompleteInFlightRequest()
    {
        inFlightRequest = null;
        TryDispatchNextRequest();
    }

    private void UpdateDeviceCache(string device, int value)
    {
        float now = Time.realtimeSinceStartup;
        deviceCache[device] = new CachedDeviceValue
        {
            Value = value & 0xFFFF,
            UpdatedAt = now
        };
        lastDataUpdateRealtime = now;
    }

    private void SendCommand(string command)
    {
        try
        {
            if (bridgeProcess == null || bridgeProcess.HasExited)
            {
                SetDisconnected("Cannot send command because the bridge is not running.");
                return;
            }

            bridgeProcess.StandardInput.WriteLine(command);
        }
        catch (Exception exception)
        {
            SetDisconnected($"IPC send failed. Code = 0x{exception.HResult:X8}\n{exception}");
        }
    }

    private void SetDisconnected(string reason)
    {
        if (!connected && Volatile.Read(ref shutdownStarted) != 0)
        {
            return;
        }

        connected = false;
        d0Ready = false;
        d101Ready = false;
        d0WriteInProgress = false;
        d0Queued = false;
        FailPendingWriteRequests();
        requestQueue.Clear();
        inFlightRequest = null;

        if (pollCoroutine != null)
        {
            StopCoroutine(pollCoroutine);
            pollCoroutine = null;
        }

        Debug.LogError($"[PLC TEST] Disconnected. {reason}");
    }

    private void FailPendingWriteRequests()
    {
        if (inFlightRequest != null && inFlightRequest.Kind == RequestKind.Write &&
            inFlightRequest.Device.Equals("D0", StringComparison.OrdinalIgnoreCase))
        {
            D0WriteCompleted?.Invoke(RequestFailureCode, inFlightRequest.Value);
            D0WriteRequestCompleted?.Invoke(
                inFlightRequest.Id,
                RequestFailureCode,
                inFlightRequest.Value);
        }

        if (inFlightRequest != null && inFlightRequest.Kind == RequestKind.Write &&
            inFlightRequest.Device.Equals("D101", StringComparison.OrdinalIgnoreCase))
        {
            D101WriteCompleted?.Invoke(inFlightRequest.Id, RequestFailureCode, inFlightRequest.Value);
        }

        foreach (PlcRequest request in requestQueue)
        {
            if (request.Kind != RequestKind.Write)
            {
                continue;
            }

            if (request.Device.Equals("D0", StringComparison.OrdinalIgnoreCase))
            {
                D0WriteCompleted?.Invoke(RequestFailureCode, request.Value);
                D0WriteRequestCompleted?.Invoke(
                    request.Id,
                    RequestFailureCode,
                    request.Value);
            }
            else if (
                request.Device.Equals("D101", StringComparison.OrdinalIgnoreCase))
            {
                D101WriteCompleted?.Invoke(request.Id, RequestFailureCode, request.Value);
            }
        }
    }

    private void OnApplicationQuit()
    {
        ShutdownBridge();
    }

    private void OnDestroy()
    {
        ShutdownBridge();
        if (activeInstance == this)
        {
            activeInstance = null;
        }
    }

    private void ShutdownBridge()
    {
        if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
        {
            return;
        }

        Debug.Log("[PLC BRIDGE] Shutdown started");
        connected = false;
        if (pollCoroutine != null)
        {
            StopCoroutine(pollCoroutine);
            pollCoroutine = null;
        }

        if (bridgeProcess == null)
        {
            return;
        }

        try
        {
            if (!bridgeProcess.HasExited)
            {
                bridgeProcess.StandardInput.WriteLine("CLOSE");
                bridgeProcess.StandardInput.Flush();
                bridgeProcess.StandardInput.Close();
                Debug.Log("[PLC BRIDGE] CLOSE sent");

                WaitHandle[] shutdownSignals =
                {
                    closeResponseReceived.WaitHandle,
                    bridgeProcessExited.WaitHandle
                };
                Stopwatch shutdownTimer = Stopwatch.StartNew();
                int signal = WaitHandle.WaitAny(shutdownSignals, 5000);
                if (signal == WaitHandle.WaitTimeout)
                {
                    Debug.LogError("[PLC TEST] Timed out waiting for CLOSE response.");
                }
                else
                {
                    int remainingMilliseconds = Math.Max(0, 5000 - (int)shutdownTimer.ElapsedMilliseconds);
                    if (!bridgeProcess.HasExited && remainingMilliseconds > 0)
                    {
                        bridgeProcess.WaitForExit(remainingMilliseconds);
                    }
                }

                if (!bridgeProcess.HasExited)
                {
                    Debug.LogError("[PLC TEST] Bridge did not exit within 5 seconds; terminating it.");
                    bridgeProcess.Kill();
                    bridgeProcess.WaitForExit(1000);
                }

                if (bridgeProcess.HasExited)
                {
                    bridgeProcess.WaitForExit();
                    Debug.Log("[PLC BRIDGE] Process exited");
                    if (closeResponseReceived.IsSet)
                    {
                        Debug.Log($"[PLC TEST] {closeResponseLine}");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PLC TEST] Bridge shutdown exception. Code = 0x{exception.HResult:X8}");
        }
        finally
        {
            bridgeProcess.OutputDataReceived -= OnBridgeOutput;
            bridgeProcess.ErrorDataReceived -= OnBridgeError;
            bridgeProcess.Exited -= OnBridgeExited;
            bridgeProcess.Dispose();
            bridgeProcess = null;
            closeResponseReceived.Dispose();
            bridgeProcessExited.Dispose();
        }
    }

    private static int ParseHex(string value)
    {
        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value.Substring(2)
            : value;
        return unchecked((int)uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

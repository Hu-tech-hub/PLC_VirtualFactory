using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public sealed class PlcConnectionTest : MonoBehaviour
{
    private const float ReadIntervalSeconds = 0.2f;

    private static PlcConnectionTest activeInstance;

    [Header("PLC Status")]
    [SerializeField] private bool connected;
    [SerializeField] private int d100Value;
    [SerializeField] private int d0Word;
    [SerializeField] private int d101LastWritten;
    [SerializeField] private string lastOpenCode = "--------";
    [SerializeField] private string lastReadCode = "--------";
    [SerializeField] private string lastWriteCode = "--------";
    [SerializeField] private string lastBridgeDiagnostic = "--------";

    private readonly ConcurrentQueue<string> responseQueue = new ConcurrentQueue<string>();
    private Process bridgeProcess;
    private readonly ManualResetEventSlim closeResponseReceived = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim bridgeProcessExited = new ManualResetEventSlim(false);
    private Coroutine readCoroutine;
    private bool readPending;
    private int shutdownStarted;
    private bool hasReadValue;
    private int pendingD101Value;
    private string closeResponseLine;
    private bool d0Ready;
    private bool d0ReadPending;
    private bool d0WriteInProgress;
    private int d0InFlightValue;
    private bool d0Queued;
    private int d0QueuedValue;

    public bool Connected => connected;
    public int D100Value => d100Value;
    public int D0Word => d0Word;
    public bool D0Ready => d0Ready;
    public int D101LastWritten => d101LastWritten;
    public string LastBridgeDiagnostic => lastBridgeDiagnostic;
    public event Action<int, int> D0WriteCompleted;

    private void Awake()
    {
        // Inspector values are diagnostic only; connection readiness must come from this Play session.
        connected = false;
        d0Ready = false;

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
        if (activeInstance != this)
        {
            return;
        }

        StartBridge();
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

    public void WriteDevice(string device, int value)
    {
        if (!connected)
        {
            Debug.LogError($"[PLC TEST] Write rejected: not connected. Device = {device}");
            return;
        }

        if (device.Equals("D101", StringComparison.OrdinalIgnoreCase))
        {
            pendingD101Value = value;
        }

        SendCommand($"WRITE {device} {value.ToString(CultureInfo.InvariantCulture)}");
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

        mask &= 0xFFFF;
        int baseValue = d0Queued
            ? d0QueuedValue
            : d0WriteInProgress
                ? d0InFlightValue
                : d0Word;
        int newValue = ((baseValue & ~mask) | (enabledBits & mask)) & 0xFFFF;

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

    private void StartD0Write(int value)
    {
        d0WriteInProgress = true;
        d0InFlightValue = value & 0xFFFF;
        WriteDevice("D0", d0InFlightValue);
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

    private IEnumerator PollD100()
    {
        WaitForSeconds wait = new WaitForSeconds(ReadIntervalSeconds);

        while (connected && Volatile.Read(ref shutdownStarted) == 0)
        {
            if (!readPending)
            {
                readPending = true;
                SendCommand("READ D100");
            }

            yield return wait;
        }
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
        d0ReadPending = true;
        SendCommand("READ D0");
        readCoroutine = StartCoroutine(PollD100());
    }

    private void HandleRead(string[] parts)
    {
        if (parts.Length < 4)
        {
            SetDisconnected("Malformed Read response.");
            return;
        }

        int result = ParseHex(parts[2]);
        lastReadCode = $"0x{result:X8}";
        if (result != 0)
        {
            SetDisconnected($"Read failed. Device = {parts[1]}, Code = {lastReadCode}");
            return;
        }

        int newValue = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (parts[1].Equals("D0", StringComparison.OrdinalIgnoreCase))
        {
            d0ReadPending = false;
            d0Word = newValue & 0xFFFF;
            d0WriteInProgress = false;
            d0Queued = false;
            d0Ready = true;
            Debug.Log($"[PLC INPUT] D0 initialized: value={d0Word}");
            return;
        }

        if (!parts[1].Equals("D100", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        readPending = false;
        if (!hasReadValue || d100Value != newValue)
        {
            d100Value = newValue;
            hasReadValue = true;
            Debug.Log($"[PLC D100] {DateTime.Now:HH:mm:ss.fff} Value changed: {parts[1]} = {d100Value}, " +
                      $"frame={Time.frameCount}, thread={Thread.CurrentThread.ManagedThreadId}, Code={lastReadCode}");
        }
    }

    private void HandleWrite(string[] parts)
    {
        if (parts.Length < 3)
        {
            SetDisconnected("Malformed Write response.");
            return;
        }

        int result = ParseHex(parts[2]);
        lastWriteCode = $"0x{result:X8}";
        bool isD0Write = parts[1].Equals("D0", StringComparison.OrdinalIgnoreCase);
        int completedD0Value = d0InFlightValue & 0xFFFF;
        if (isD0Write)
        {
            Debug.Log($"[PLC INPUT] WRITE D0 result: {lastWriteCode}");
        }

        if (result != 0)
        {
            if (isD0Write)
            {
                D0WriteCompleted?.Invoke(result, completedD0Value);
                d0WriteInProgress = false;
                d0Queued = false;
            }

            SetDisconnected($"Write failed. Device = {parts[1]}, Code = {lastWriteCode}");
            return;
        }

        if (parts[1].Equals("D101", StringComparison.OrdinalIgnoreCase))
        {
            d101LastWritten = pendingD101Value;
        }
        else if (isD0Write)
        {
            d0Word = completedD0Value;
            d0WriteInProgress = false;
            D0WriteCompleted?.Invoke(result, d0Word);
            TryStartQueuedD0Write();
        }

        Debug.Log($"[PLC TEST] Write succeeded. Device = {parts[1]}, Code = {lastWriteCode}");
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
        readPending = false;
        d0ReadPending = false;
        d0Ready = false;
        d0WriteInProgress = false;
        d0Queued = false;
        if (readCoroutine != null)
        {
            StopCoroutine(readCoroutine);
            readCoroutine = null;
        }

        Debug.LogError($"[PLC TEST] Disconnected. {reason}");
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

        if (readCoroutine != null)
        {
            StopCoroutine(readCoroutine);
            readCoroutine = null;
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
                    if (closeResponseReceived.IsSet)
                    {
                        Debug.Log("[PLC BRIDGE] CLOSE acknowledged");
                    }

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
                    // Drain redirected output before detaching handlers and disposing wait handles.
                    bridgeProcess.WaitForExit();
                    Debug.Log("[PLC BRIDGE] Process exited");
                    if (closeResponseReceived.IsSet)
                    {
                        Debug.Log($"[PLC TEST] {closeResponseLine}");
                    }

                    Debug.Log("[PLC BRIDGE] Mutex released");
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

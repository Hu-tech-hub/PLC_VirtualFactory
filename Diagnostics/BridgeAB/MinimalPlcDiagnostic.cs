using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ActUtlType64Lib;

internal static class MinimalPlcDiagnostic
{
    [STAThread]
    private static int Main()
    {
        int exitCode = 3;

        using (Form form = new Form())
        {
            form.ShowInTaskbar = false;
            form.WindowState = FormWindowState.Minimized;
            form.Shown += delegate
            {
                form.BeginInvoke(new Action(delegate
                {
                    exitCode = RunOnce();
                    Application.ExitThread();
                }));
            };
            Application.Run(form);
        }

        return exitCode;
    }

    private static int RunOnce()
    {
        ActUtlType64Class plc = null;
        bool opened = false;

        try
        {
            Console.WriteLine("CREATING");
            Console.Out.Flush();
            plc = new ActUtlType64Class { ActLogicalStationNumber = 1 };
            Console.WriteLine("CREATED");
            Console.WriteLine("CALLING_OPEN");
            Console.Out.Flush();

            int openResult = plc.Open();
            Console.WriteLine("OPEN=0x{0:X8}", openResult);
            Console.Out.Flush();
            if (openResult != 0)
            {
                return 1;
            }

            opened = true;
            int value;
            int readResult = plc.GetDevice("D100", out value);
            Console.WriteLine("READ=0x{0:X8};D100={1}", readResult, value);
            Console.Out.Flush();
            return readResult == 0 ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.WriteLine("EXCEPTION=0x{0:X8};MESSAGE={1}", exception.HResult, exception.Message);
            Console.Out.Flush();
            return 3;
        }
        finally
        {
            if (opened && plc != null)
            {
                plc.Close();
            }

            if (plc != null)
            {
                Marshal.FinalReleaseComObject(plc);
            }
        }
    }
}

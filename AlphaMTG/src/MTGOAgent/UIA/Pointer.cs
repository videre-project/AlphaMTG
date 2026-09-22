using System.ComponentModel;
using System.Runtime.InteropServices;

using FlaUI.Core.Input;


namespace AlphaMTG.MTGOAgent.UIA;

internal static class Pointer
{
  private const uint LeftDown = 0x0201;
  private const uint LeftUp = 0x0202;
  private const uint RightDown = 0x0204;
  private const uint RightUp = 0x0205;
  private const int LeftButton = 0x0001;
  private const int RightButton = 0x0002;

  public static void Click(
    IntPtr window,
    System.Drawing.Point point,
    MouseButton button
  )
  {
    Mouse.MoveTo(point);
    Thread.Sleep(100);

    var nativePoint = new NativePoint(point.X, point.Y);
    if (window == IntPtr.Zero)
      throw new InvalidOperationException("The MTGO window has no native handle.");

    if (!ScreenToClient(window, ref nativePoint))
      throw new Win32Exception(Marshal.GetLastWin32Error());

    var (down, up, state) = button switch
    {
      MouseButton.Left => (LeftDown, LeftUp, LeftButton),
      MouseButton.Right => (RightDown, RightUp, RightButton),
      _ => throw new ArgumentException($"Unsupported mouse button '{button}'.")
    };
    var position = new IntPtr(
      (nativePoint.Y << 16) | (nativePoint.X & 0xffff)
    );

    Send(window, down, new IntPtr(state), position);
    Thread.Sleep(100);
    Send(window, up, IntPtr.Zero, position);
  }

  private static void Send(
    IntPtr window,
    uint message,
    IntPtr state,
    IntPtr position
  )
  {
    if (!PostMessage(window, message, state, position))
      throw new Win32Exception(Marshal.GetLastWin32Error());
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativePoint(int x, int y)
  {
    public int X = x;
    public int Y = y;
  }

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool ScreenToClient(
    IntPtr window,
    ref NativePoint point
  );

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool PostMessage(
    IntPtr window,
    uint message,
    IntPtr state,
    IntPtr position
  );
}

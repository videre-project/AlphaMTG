/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Globalization;
using System.IO;


namespace AlphaMTG.MTGOAgent;

/// <summary>
/// An exclusive, cross-process claim on being the one agent that writes the
/// shared state files.
/// </summary>
/// <remarks>
/// The exporter writes to a fixed path, so a second agent process attached to
/// the same MTGO client silently overwrites the first one's output. The
/// diagnostic subcommands are meant to run alongside the agent and do not
/// take this lock; only the agent does.
/// </remarks>
internal sealed class AgentInstanceLock : IDisposable
{
  private readonly FileStream _stream;

  private AgentInstanceLock(FileStream stream)
  {
    _stream = stream;
  }

  public static string DefaultPath =>
    Environment.GetEnvironmentVariable("ALPHAMTG_LOCK_PATH")
    ?? Path.Combine(
      Path.GetDirectoryName(Path.GetFullPath(AgentStateStore.DefaultPath))!,
      "alphamtg.lock");

  /// <summary>
  /// Attempts to become the single writing agent.
  /// </summary>
  /// <param name="path">The lock file to claim.</param>
  /// <param name="instanceLock">The claim, or null when already held.</param>
  /// <param name="holder">
  /// A description of the current holder, for the error message.
  /// </param>
  public static bool TryAcquire(
    string path,
    out AgentInstanceLock? instanceLock,
    out string? holder)
  {
    instanceLock = null;
    holder = null;

    string fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

    FileStream stream;
    try
    {
      // Read sharing is allowed so a second process can read who holds the
      // lock and report it. Write sharing is not, so the OS refuses a second
      // writer rather than letting two agents both believe they own the
      // state files.
      stream = new FileStream(
        fullPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.Read);
    }
    catch (IOException)
    {
      holder = ReadHolder(fullPath);
      return false;
    }
    catch (UnauthorizedAccessException)
    {
      holder = ReadHolder(fullPath);
      return false;
    }

    try
    {
      stream.SetLength(0);
      byte[] payload = System.Text.Encoding.UTF8.GetBytes(
        string.Create(
          CultureInfo.InvariantCulture,
          $"pid={Environment.ProcessId} acquired={DateTimeOffset.UtcNow:O}"));
      stream.Write(payload, 0, payload.Length);
      stream.Flush();
    }
    catch
    {
      // The claim itself is the lock. Failing to annotate it is not fatal.
    }

    instanceLock = new AgentInstanceLock(stream);
    return true;
  }

  private static string? ReadHolder(string path)
  {
    try
    {
      using var reader = new StreamReader(
        new FileStream(
          path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
      string contents = reader.ReadToEnd().Trim();
      return contents.Length == 0 ? "another AlphaMTG instance" : contents;
    }
    catch
    {
      return "another AlphaMTG instance";
    }
  }

  public void Dispose()
  {
    try
    {
      _stream.Dispose();
    }
    catch
    {
      // Nothing useful to do while shutting down.
    }
  }
}

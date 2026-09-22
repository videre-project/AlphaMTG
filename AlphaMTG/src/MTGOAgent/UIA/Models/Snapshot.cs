namespace AlphaMTG.MTGOAgent.UIA.Models;

internal sealed record Snapshot(
  string Id,
  IReadOnlyList<Node> Nodes
);

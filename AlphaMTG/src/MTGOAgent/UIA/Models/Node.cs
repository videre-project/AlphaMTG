namespace AlphaMTG.MTGOAgent.UIA.Models;

internal sealed record Node(
  string Reference,
  string Root,
  IReadOnlyList<int> Path,
  string? AutomationId,
  string? Name,
  string? ClassName,
  string ControlType,
  bool IsEnabled,
  Bounds Bounds,
  Point? ClickPoint,
  IReadOnlyList<string> Operations,
  CardView? Card,
  int? CardId
);
